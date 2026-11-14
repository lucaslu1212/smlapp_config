using SmlServer.Models;

namespace SmlServer.Services;

/// <summary>卸载 / 退出程序的在线审批。</summary>
public sealed class ApprovalService
{
    /// <summary>申请本身的有效期：超过这个时间未处理自动过期。</summary>
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);

    /// <summary>教师批准后 grant 的有效期（客户端要求 90 秒内有效）。</summary>
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromSeconds(90);

    private readonly DataStore _store;
    private readonly ConnectionManager _connections;
    private readonly AuditService _audit;

    public ApprovalService(DataStore store, ConnectionManager connections, AuditService audit)
    {
        _store = store;
        _connections = connections;
        _audit = audit;
    }

    public ApprovalRecord Create(string kind, string deviceId, string deviceName, string reason, string requestId)
    {
        var record = new ApprovalRecord
        {
            RequestId = requestId,
            Kind = kind,
            DeviceId = deviceId,
            DeviceName = deviceName,
            Reason = reason,
            ExpiresAt = DateTimeOffset.UtcNow.Add(RequestLifetime).ToUnixTimeMilliseconds(),
        };

        _store.Mutate(s => s.Approvals.Add(record));

        _audit.Write("device", "device", kind + ".request", deviceId,
            $"设备发起{KindText(kind)}申请，requestId={requestId}", string.Empty);

        return record;
    }

    public List<ApprovalRecord> List(string? status, string? deviceId, int limit)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return _store.Read(s =>
        {
            foreach (var pending in s.Approvals.Where(a => a.IsPending && a.ExpiresAt > 0 && a.ExpiresAt <= now))
            {
                pending.Status = ApprovalStatus.Expired;
                pending.DecidedAt = now;
            }

            return s.Approvals
                .Where(a => string.IsNullOrEmpty(status) || a.Status == status)
                .Where(a => string.IsNullOrEmpty(deviceId) || a.DeviceId == deviceId)
                .OrderByDescending(a => a.RequestedAt)
                .Take(Math.Clamp(limit, 1, 200))
                .ToList();
        });
    }

    /// <summary>教师批准/拒绝，并把 grant/deny 发到发起申请的连接。</summary>
    public (bool Ok, string? Error, bool Delivered) Decide(string requestId, bool approve, UserRecord actor, string? note)
    {
        ApprovalRecord? record;
        var error = default(string);

        record = _store.Mutate(s =>
        {
            var target = s.Approvals.FirstOrDefault(a => a.RequestId == requestId);
            if (target is null)
            {
                error = "审批请求不存在";
                return null;
            }

            if (!target.IsPending)
            {
                error = "该申请已处理或已过期";
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (target.ExpiresAt > 0 && target.ExpiresAt <= now.ToUnixTimeMilliseconds())
            {
                target.Status = ApprovalStatus.Expired;
                target.DecidedAt = now.ToUnixTimeMilliseconds();
                error = "该申请已过期";
                return null;
            }

            target.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Denied;
            target.DecidedBy = string.IsNullOrEmpty(actor.DisplayName) ? actor.Username : actor.DisplayName;
            target.DecidedAt = now.ToUnixTimeMilliseconds();
            target.DecisionNote = note;
            return target;
        });

        if (record is null)
            return (false, error ?? "处理失败", false);

        var delivered = SendDecision(record, approve, actor, note).GetAwaiter().GetResult();

        _audit.Write(actor.Username, actor.Role,
            approve ? record.Kind + ".approve" : record.Kind + ".deny",
            record.DeviceId,
            $"{KindText(record.Kind)}申请 requestId={record.RequestId}，{(approve ? "已批准" : "已拒绝")}{(delivered ? "" : "（设备连接已断开，未能送达）")}",
            string.Empty);

        return (true, null, delivered);
    }

    private async Task<bool> SendDecision(ApprovalRecord record, bool approve, UserRecord actor, string? note)
    {
        var teacherName = string.IsNullOrEmpty(actor.DisplayName) ? actor.Username : actor.DisplayName;
        var expiresAt = DateTimeOffset.UtcNow.Add(GrantLifetime).ToUnixTimeMilliseconds();

        Envelope envelope;
        if (record.Kind == ApprovalKind.Uninstall)
        {
            envelope = approve
                ? Envelope.Create(Downstream.UninstallGrant, new UninstallGrantPayload
                {
                    RequestId = record.RequestId,
                    TeacherId = actor.Id,
                    TeacherName = teacherName,
                    ExpiresAt = expiresAt,
                })
                : Envelope.Create(Downstream.UninstallDeny, new UninstallDenyPayload
                {
                    RequestId = record.RequestId,
                    Reason = note,
                });
        }
        else
        {
            envelope = approve
                ? Envelope.Create(Downstream.ExitGrant, new ExitGrantPayload
                {
                    RequestId = record.RequestId,
                    TeacherId = actor.Id,
                    TeacherName = teacherName,
                    ExpiresAt = expiresAt,
                })
                : Envelope.Create(Downstream.ExitDeny, new ExitDenyPayload
                {
                    RequestId = record.RequestId,
                    Reason = note,
                });
        }

        return await _connections.SendToApprovalChannelAsync(record.RequestId, record.DeviceId, envelope);
    }

    private static string KindText(string kind)
        => kind == ApprovalKind.Uninstall ? "卸载" : "退出程序";
}
