using System.Security.Claims;
using System.Text.Json;
using SmlServer.Models;
using SmlServer.Services;

namespace SmlServer.Endpoints;

public sealed class CommandRequest
{
    public string Type { get; set; } = string.Empty;
    public JsonElement? Data { get; set; }
}

public sealed class OtpCreateBody
{
    public string ClassroomId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public int Count { get; set; } = 1;
    public int MaxUses { get; set; } = 1;
    public int ValidMinutes { get; set; } = 4320;
    public string? Note { get; set; }
}

public sealed class DecisionBody
{
    public string? Note { get; set; }
}

/// <summary>教师端接口：设备、指令、策略、一次性密码、审批、截屏。</summary>
public static class TeacherEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api").RequireAuthorization("TeacherOrAdmin");

        group.MapGet("/classrooms", (HttpContext context, DataStore store) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var list = store.Read(s => s.Classrooms
                .Where(c => user.Role == Roles.Admin || user.ClassroomIds.Contains(c.Id))
                .OrderBy(c => c.Name)
                .ToList());

            return Results.Ok(list);
        });

        group.MapGet("/devices", (HttpContext context, DataStore store, ConnectionManager connections) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var devices = store.Read(s => s.Devices
                .Where(d => CanAccess(user, d))
                .OrderBy(d => d.Name)
                .ToList());

            var classrooms = store.Read(s => s.Classrooms.ToDictionary(c => c.Id, c => c.Name));

            var result = devices.Select(d =>
            {
                var conn = connections.GetPrimary(d.DeviceId);
                var online = conn is not null;
                return new
                {
                    deviceId = d.DeviceId,
                    name = string.IsNullOrWhiteSpace(d.Name) ? d.DeviceId : d.Name,
                    classroomId = d.ClassroomId,
                    classroomName = classrooms.TryGetValue(d.ClassroomId, out var name) ? name : d.ClassroomId,
                    classroomLabel = d.ClassroomLabel,
                    enabled = d.Enabled,
                    bound = d.Bound,
                    online,
                    lastSeenAt = online ? conn!.LastSeen.ToUnixTimeMilliseconds() : d.LastSeenAt,
                    lastIp = online ? conn!.RemoteIp : d.LastIp,
                    appVersion = d.AppVersion,
                    machineUser = d.MachineUser,
                    runtime = online ? conn!.Runtime : d.Runtime,
                };
            });

            return Results.Ok(result);
        });

        group.MapGet("/devices/{deviceId}", (string deviceId, HttpContext context, DataStore store, ConnectionManager connections) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var device = store.Read(s => s.Devices.FirstOrDefault(d => d.DeviceId == deviceId));
            if (device is null || !CanAccess(user, device)) return Results.NotFound(new { error = "设备不存在" });

            var conn = connections.GetPrimary(deviceId);
            return Results.Ok(new
            {
                deviceId = device.DeviceId,
                name = device.Name,
                classroomId = device.ClassroomId,
                classroomLabel = device.ClassroomLabel,
                enabled = device.Enabled,
                bound = device.Bound,
                online = conn is not null,
                lastSeenAt = conn?.LastSeen.ToUnixTimeMilliseconds() ?? device.LastSeenAt,
                lastIp = conn?.RemoteIp ?? device.LastIp,
                appVersion = device.AppVersion,
                osVersion = device.OsVersion,
                machineUser = device.MachineUser,
                runtime = conn?.Runtime ?? device.Runtime,
                policy = device.Policy,
                tokenHint = device.TokenHint,
            });
        });

        group.MapGet("/devices/{deviceId}/events", (string deviceId, int? limit, HttpContext context, DataStore store, DeviceEventService events) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var device = store.Read(s => s.Devices.FirstOrDefault(d => d.DeviceId == deviceId));
            if (device is null || !CanAccess(user, device)) return Results.NotFound(new { error = "设备不存在" });

            return Results.Ok(events.Recent(deviceId, limit ?? 50));
        });

        group.MapPost("/devices/{deviceId}/command", async (
            string deviceId, CommandRequest request, HttpContext context,
            DataStore store, ConnectionManager connections, AuditService audit) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var device = store.Read(s => s.Devices.FirstOrDefault(d => d.DeviceId == deviceId));
            if (device is null || !CanAccess(user, device))
                return Results.NotFound(new { error = "设备不存在" });

            if (!device.Enabled)
                return Results.BadRequest(new { error = "设备已禁用" });

            var built = BuildCommand(request);
            if (built.Error is not null)
                return Results.BadRequest(new { error = built.Error });

            var timeout = request.Type == Downstream.Screen ? TimeSpan.FromSeconds(35) : TimeSpan.FromSeconds(15);
            var result = await connections.SendCommandAsync(deviceId, built.Envelope!, timeout, context.RequestAborted);

            audit.Write(user.Username, user.Role, "device.command", deviceId,
                $"{request.Type}: {(result.Ok ? "成功" : "失败")} {result.Error}", context.Connection.RemoteIpAddress?.ToString() ?? "");

            return Results.Ok(new
            {
                ok = result.Ok,
                error = result.Error,
                note = result.Note,
                requestId = built.RequestId,
            });
        });

        group.MapPost("/devices/{deviceId}/policy", async (
            string deviceId, PolicyUpdatePayload policy, HttpContext context,
            DataStore store, ConnectionManager connections, AuditService audit) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var device = store.Read(s => s.Devices.FirstOrDefault(d => d.DeviceId == deviceId));
            if (device is null || !CanAccess(user, device))
                return Results.NotFound(new { error = "设备不存在" });

            policy.App = Normalize(policy.App);
            policy.Network = Normalize(policy.Network);

            store.Mutate(s =>
            {
                var target = s.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (target is not null)
                    target.Policy = new StoredPolicy { App = policy.App, Network = policy.Network };
            });

            var sent = false;
            var conn = connections.GetPrimary(deviceId);
            if (conn is not null)
            {
                try
                {
                    await connections.SendAsync(conn, Envelope.Create(Downstream.PolicyUpdate, policy));
                    sent = true;
                }
                catch
                {
                    // 设备可能刚刚断开
                }
            }

            audit.Write(user.Username, user.Role, "policy.update", deviceId,
                $"策略已保存{(sent ? "并下发" : "，设备离线未下发")}", context.Connection.RemoteIpAddress?.ToString() ?? "");

            return Results.Ok(new { ok = true, delivered = sent });
        });

        group.MapGet("/screens/{requestId}", (string requestId, ServerOptions options, HttpContext context, DataStore store) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var file = ScreenshotPath(requestId, options);
            return File.Exists(file)
                ? Results.File(file, "image/jpeg")
                : Results.NotFound(new { error = "截图不存在或尚未上传" });
        });

        // ---- 一次性密码 ----

        group.MapGet("/otp", (string? classroomId, string? deviceId, bool? activeOnly, HttpContext context, DataStore store, OtpService otp) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var scope = ResolveScope(user, classroomId, deviceId);
            if (scope.Error is not null) return Results.BadRequest(new { error = scope.Error });

            var list = otp.List(scope.ClassroomId, deviceId, activeOnly ?? false)
                .Where(o => user.Role == Roles.Admin || user.ClassroomIds.Contains(o.ClassroomId) || o.ClassroomId.Length == 0)
                .ToList();

            return Results.Ok(list.Select(ToOtpDto));
        });

        group.MapPost("/otp", async (OtpCreateBody body, HttpContext context,
            DataStore store, OtpService otp, ConnectionManager connections, AuditService audit) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var scope = ResolveScope(user, body.ClassroomId, body.DeviceId);
            if (scope.Error is not null) return Results.BadRequest(new { error = scope.Error });

            // 指定了设备时，必须在该教师可访问的范围内
            if (!string.IsNullOrWhiteSpace(body.DeviceId))
            {
                var target = store.Read(s => s.Devices.FirstOrDefault(d => d.DeviceId == body.DeviceId));
                if (target is null || !CanAccess(user, target))
                    return Results.BadRequest(new { error = "设备不存在或无权操作" });
            }

            var (plain, records) = otp.Generate(user, new CreateOtpRequest
            {
                ClassroomId = scope.ClassroomId,
                DeviceId = body.DeviceId ?? string.Empty,
                Count = body.Count,
                MaxUses = body.MaxUses,
                ValidMinutes = body.ValidMinutes,
                Note = body.Note,
            });

            // 推送给受影响的在线设备，避免等下一次 otp.pull
            var affected = store.Read(s => s.Devices
                .Where(d => string.IsNullOrEmpty(scope.ClassroomId) || d.ClassroomId == scope.ClassroomId)
                .Where(d => string.IsNullOrEmpty(body.DeviceId) || d.DeviceId == body.DeviceId)
                .ToList());

            foreach (var device in affected)
            {
                var conn = connections.GetPrimary(device.DeviceId);
                if (conn is null) continue;
                try
                {
                    await connections.SendAsync(conn,
                        Envelope.Create(Downstream.OtpSync, otp.BuildSync(device.DeviceId, device.ClassroomId)));
                }
                catch
                {
                    // 忽略单台失败
                }
            }

            audit.Write(user.Username, user.Role, "otp.generate", scope.ClassroomId,
                $"生成 {plain.Count} 个码，推送给 {affected.Count} 台设备", context.Connection.RemoteIpAddress?.ToString() ?? "");

            return Results.Ok(new
            {
                codes = plain,
                otpIds = records.Select(r => r.OtpId),
                expiresAt = records.FirstOrDefault()?.ExpiresAt ?? 0,
                maxUses = records.FirstOrDefault()?.MaxUses ?? 1,
            });
        });

        group.MapDelete("/otp/{otpId}", (string otpId, HttpContext context, DataStore store, OtpService otp) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var record = store.Read(s => s.Otps.FirstOrDefault(o => o.OtpId == otpId));
            if (record is null) return Results.NotFound(new { error = "不存在" });
            if (user.Role != Roles.Admin && !user.ClassroomIds.Contains(record.ClassroomId))
                return Results.Forbid();

            return otp.Revoke(otpId, user)
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new { error = "不存在" });
        });

        group.MapGet("/otp/usages", (string? deviceId, int? limit, HttpContext context, DataStore store, OtpService otp) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            if (user.Role == Roles.Admin)
                return Results.Ok(otp.ListUsages(deviceId, limit ?? 100));

            var allowed = store.Read(s => s.Devices
                .Where(d => user.ClassroomIds.Contains(d.ClassroomId))
                .Select(d => d.DeviceId)
                .ToHashSet());

            var usages = otp.ListUsages(deviceId, limit ?? 100)
                .Where(u => allowed.Contains(u.DeviceId))
                .ToList();

            return Results.Ok(usages);
        });

        // ---- 审批 ----

        group.MapGet("/approvals", (string? status, string? deviceId, HttpContext context,
            DataStore store, ApprovalService approvals) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            var allowed = store.Read(s => s.Devices
                .Where(d => CanAccess(user, d))
                .Select(d => d.DeviceId)
                .ToHashSet());

            var list = approvals.List(status, deviceId, 100)
                .Where(a => user.Role == Roles.Admin || allowed.Contains(a.DeviceId))
                .ToList();

            return Results.Ok(list);
        });

        group.MapPost("/approvals/{requestId}/approve", (string requestId, DecisionBody? body, HttpContext context,
            DataStore store, ApprovalService approvals) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            if (!CanDecide(user, requestId, store))
                return Results.Forbid();

            var result = approvals.Decide(requestId, approve: true, user, body?.Note);
            return result.Ok
                ? Results.Ok(new { ok = true, delivered = result.Delivered })
                : Results.BadRequest(new { error = result.Error });
        });

        group.MapPost("/approvals/{requestId}/deny", (string requestId, DecisionBody? body, HttpContext context,
            DataStore store, ApprovalService approvals) =>
        {
            var user = AuthEndpoints.CurrentUser(context, store);
            if (user is null) return Results.Unauthorized();

            if (!CanDecide(user, requestId, store))
                return Results.Forbid();

            var result = approvals.Decide(requestId, approve: false, user, body?.Note);
            return result.Ok
                ? Results.Ok(new { ok = true, delivered = result.Delivered })
                : Results.BadRequest(new { error = result.Error });
        });
    }

    // ---- 辅助 ----

    private static bool CanAccess(UserRecord user, DeviceRecord device)
        => user.Role == Roles.Admin ||
           (!string.IsNullOrEmpty(device.ClassroomId) && user.ClassroomIds.Contains(device.ClassroomId));

    private static bool CanDecide(UserRecord user, string requestId, DataStore store)
    {
        if (user.Role == Roles.Admin) return true;

        var approval = store.Read(s => s.Approvals.FirstOrDefault(a => a.RequestId == requestId));
        if (approval is null) return false;

        var device = store.Read(s => s.Devices.FirstOrDefault(d => d.DeviceId == approval.DeviceId));
        return device is not null && CanAccess(user, device);
    }

    private static (string ClassroomId, string? Error) ResolveScope(UserRecord user, string? classroomId, string? deviceId)
    {
        var cid = classroomId?.Trim() ?? string.Empty;

        if (user.Role == Roles.Admin)
            return (cid, null);

        if (cid.Length > 0)
            return user.ClassroomIds.Contains(cid) ? (cid, null) : (cid, "无权操作该教室");

        if (user.ClassroomIds.Count == 1)
            return (user.ClassroomIds[0], null);

        return (cid, "请指定教室");
    }

    private static (Envelope? Envelope, string? RequestId, string? Error) BuildCommand(CommandRequest request)
    {
        var data = request.Data;

        switch (request.Type)
        {
            case Downstream.CallStudent:
            {
                var payload = data is { } el ? el.Deserialize<CallStudentPayload>(Json.Options) : new CallStudentPayload();
                payload ??= new CallStudentPayload();
                return (Envelope.Create(Downstream.CallStudent, payload), null, null);
            }

            case Downstream.ShowMessage:
            {
                var payload = data is { } el ? el.Deserialize<ShowMessagePayload>(Json.Options) : new ShowMessagePayload();
                payload ??= new ShowMessagePayload();
                if (string.IsNullOrWhiteSpace(payload.Text))
                    return (null, null, "通知内容不能为空");
                payload.DurationSeconds = Math.Clamp(payload.DurationSeconds, 0, 300);
                return (Envelope.Create(Downstream.ShowMessage, payload), null, null);
            }

            case Downstream.PowerLock:
            case Downstream.PowerShutdown:
            case Downstream.PowerRestart:
            {
                var payload = data is { } el ? el.Deserialize<PowerPayload>(Json.Options) : new PowerPayload();
                payload ??= new PowerPayload();
                payload.DelaySeconds = Math.Clamp(payload.DelaySeconds, 0, 300);
                return (Envelope.Create(request.Type, payload), null, null);
            }

            case Downstream.Volume:
            {
                var payload = data is { } el ? el.Deserialize<VolumePayload>(Json.Options) : new VolumePayload();
                payload ??= new VolumePayload();
                payload.Percent = Math.Clamp(payload.Percent, 0, 100);
                return (Envelope.Create(Downstream.Volume, payload), null, null);
            }

            case Downstream.Screen:
            {
                var payload = data is { } el ? el.Deserialize<ScreenRequestPayload>(Json.Options) : new ScreenRequestPayload();
                payload ??= new ScreenRequestPayload();
                payload.RequestId = Guid.NewGuid().ToString("N")[..12];
                payload.MaxWidth = Math.Clamp(payload.MaxWidth, 320, 3840);
                payload.Quality = Math.Clamp(payload.Quality, 30, 95);
                return (Envelope.Create(Downstream.Screen, payload), payload.RequestId, null);
            }

            default:
                return (null, null, "不支持的指令类型");
        }
    }

    private static AppPolicy? Normalize(AppPolicy? policy)
    {
        if (policy is null) return null;
        policy.Blacklist = Clean(policy.Blacklist);
        policy.Whitelist = Clean(policy.Whitelist);
        policy.ScanIntervalSeconds = Math.Clamp(policy.ScanIntervalSeconds, 1, 60);
        return policy;
    }

    private static NetworkPolicy? Normalize(NetworkPolicy? policy)
    {
        if (policy is null) return null;
        policy.DomainBlacklist = Clean(policy.DomainBlacklist);
        policy.DomainWhitelist = Clean(policy.DomainWhitelist);
        policy.IpBlacklist = Clean(policy.IpBlacklist);
        policy.IpWhitelist = Clean(policy.IpWhitelist);
        return policy;
    }

    private static List<string> Clean(List<string>? source) => (source ?? new List<string>())
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => s.Trim().ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static object ToOtpDto(OtpRecord record) => new
    {
        otpId = record.OtpId,
        classroomId = record.ClassroomId,
        deviceId = record.DeviceId,
        maxUses = record.MaxUses,
        usedCount = record.UsedCount,
        expiresAt = record.ExpiresAt,
        note = record.Note,
        issuedBy = record.IssuedBy,
        issuedAt = record.IssuedAt,
        revoked = record.Revoked,
        available = !record.Revoked && record.UsedCount < Math.Max(1, record.MaxUses) &&
                    (record.ExpiresAt <= 0 || record.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
    };

    private static string ScreenshotPath(string requestId, ServerOptions options)
    {
        var safe = string.Concat(requestId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
        if (safe.Length == 0) safe = "unknown";
        return Path.Combine(AppContext.BaseDirectory, options.DataDir, "screens", safe + ".jpg");
    }
}
