using SmlServer.Models;

namespace SmlServer.Services;

public sealed class CreateOtpRequest
{
    public string ClassroomId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public int Count { get; set; } = 1;
    public int MaxUses { get; set; } = 1;
    public int ValidMinutes { get; set; } = 4320;
    public string? Note { get; set; }
}

/// <summary>一次性密码：生成、同步给设备、核销使用记录。</summary>
public sealed class OtpService
{
    private readonly DataStore _store;
    private readonly AuditService _audit;

    public OtpService(DataStore store, AuditService audit)
    {
        _store = store;
        _audit = audit;
    }

    /// <summary>生成 count 个码，返回明文（仅此一次）；同时返回记录便于推送。</summary>
    public (List<string> PlainCodes, List<OtpRecord> Records) Generate(UserRecord issuer, CreateOtpRequest request)
    {
        var count = Math.Clamp(request.Count, 1, 10);
        var maxUses = Math.Clamp(request.MaxUses, 1, 10);
        var validMinutes = Math.Clamp(request.ValidMinutes, 5, 43200);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(validMinutes).ToUnixTimeMilliseconds();

        var plain = new List<string>();
        var records = new List<OtpRecord>();

        _store.Mutate(s =>
        {
            for (var i = 0; i < count; i++)
            {
                var code = Security.NewOtpPlain();
                var record = new OtpRecord
                {
                    CodeHash = Security.HashOtpCode(Security.NormalizeOtpCode(code)),
                    ClassroomId = request.ClassroomId ?? string.Empty,
                    DeviceId = request.DeviceId ?? string.Empty,
                    MaxUses = maxUses,
                    ExpiresAt = expiresAt,
                    Note = request.Note,
                    IssuedBy = issuer.DisplayName.Length > 0 ? issuer.DisplayName : issuer.Username,
                };

                s.Otps.Add(record);
                plain.Add(code);
                records.Add(record);
            }
        });

        _audit.Write(issuer.Username, issuer.Role, "otp.generate",
            string.IsNullOrEmpty(request.DeviceId) ? (string.IsNullOrEmpty(request.ClassroomId) ? "*" : request.ClassroomId) : request.DeviceId,
            $"生成 {count} 个，每个可用 {maxUses} 次，有效期 {validMinutes} 分钟", string.Empty);

        return (plain, records);
    }

    public List<OtpRecord> List(string? classroomId, string? deviceId, bool activeOnly)
    {
        return _store.Read(s =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return s.Otps
                .Where(o => string.IsNullOrEmpty(classroomId) || o.ClassroomId == classroomId)
                .Where(o => string.IsNullOrEmpty(deviceId) || o.DeviceId == deviceId)
                .Where(o => !activeOnly || IsAvailable(o, now))
                .OrderByDescending(o => o.IssuedAt)
                .Take(200)
                .ToList();
        });
    }

    public bool Revoke(string otpId, UserRecord actor)
    {
        var ok = _store.Mutate(s =>
        {
            var record = s.Otps.FirstOrDefault(o => o.OtpId == otpId);
            if (record is null) return false;
            record.Revoked = true;
            record.RevokedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return true;
        });

        if (ok)
            _audit.Write(actor.Username, actor.Role, "otp.revoke", otpId, "吊销一次性密码", string.Empty);

        return ok;
    }

    /// <summary>某台设备当前可用的码（otp.sync）。</summary>
    public OtpSyncPayload BuildSync(string deviceId, string classroomId, bool replace = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var codes = _store.Read(s => s.Otps
            .Where(o => IsAvailable(o, now))
            .Where(o => string.IsNullOrEmpty(o.DeviceId) || o.DeviceId == deviceId)
            .Where(o => string.IsNullOrEmpty(o.ClassroomId) || o.ClassroomId == classroomId)
            .Select(o => new OtpCodePayload
            {
                OtpId = o.OtpId,
                Hash = o.CodeHash,
                ExpiresAt = o.ExpiresAt,
                IssuedBy = o.IssuedBy,
                Note = o.Note,
                MaxUses = o.MaxUses,
            })
            .ToList());

        return new OtpSyncPayload { Replace = replace, Codes = codes };
    }

    /// <summary>处理 otp.report：按 usageId 幂等，成功记录核销该码（全局失效）。</summary>
    public int HandleReport(string deviceId, OtpReportPayload payload)
    {
        var applied = 0;

        _store.Mutate(s =>
        {
            foreach (var usage in payload.Usages)
            {
                if (string.IsNullOrWhiteSpace(usage.UsageId)) continue;
                if (s.OtpUsages.Any(u => u.UsageId == usage.UsageId)) continue;

                s.OtpUsages.Add(new OtpUsageRecord
                {
                    UsageId = usage.UsageId,
                    OtpId = usage.OtpId,
                    DeviceId = deviceId,
                    Action = usage.Action,
                    Result = usage.Result,
                    AttemptHash = usage.AttemptHash,
                    At = usage.UsedAt > 0 ? usage.UsedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });

                if (usage.Result == "success" && !string.IsNullOrEmpty(usage.OtpId))
                {
                    var record = s.Otps.FirstOrDefault(o => o.OtpId == usage.OtpId);
                    if (record is not null)
                    {
                        record.UsedCount++;
                        if (record.UsedCount >= Math.Max(1, record.MaxUses))
                        {
                            record.Revoked = true;
                            record.RevokedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        }
                    }
                }

                applied++;
            }
        });

        if (applied > 0)
        {
            _audit.Write("device", "device", "otp.report", deviceId,
                $"上报 {applied} 条一次性密码使用记录", string.Empty);
        }

        return applied;
    }

    public List<OtpUsageRecord> ListUsages(string? deviceId, int limit)
    {
        return _store.Read(s => s.OtpUsages
            .Where(u => string.IsNullOrEmpty(deviceId) || u.DeviceId == deviceId)
            .OrderByDescending(u => u.At)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList());
    }

    private static bool IsAvailable(OtpRecord o, long now)
        => !o.Revoked
           && o.UsedCount < Math.Max(1, o.MaxUses)
           && (o.ExpiresAt <= 0 || o.ExpiresAt > now);
}
