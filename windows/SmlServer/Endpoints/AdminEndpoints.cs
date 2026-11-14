using System.Security.Claims;
using SmlServer.Models;
using SmlServer.Services;

namespace SmlServer.Endpoints;

public sealed class CreateDeviceBody
{
    public string Name { get; set; } = string.Empty;
    public string ClassroomId { get; set; } = string.Empty;
}

public sealed class PatchDeviceBody
{
    public string? Name { get; set; }
    public string? ClassroomId { get; set; }
    public bool? Enabled { get; set; }
}

public sealed class CreateClassroomBody
{
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public sealed class CreateUserBody
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = Roles.Teacher;
    public List<string> ClassroomIds { get; set; } = new();
}

public sealed class PatchUserBody
{
    public string? DisplayName { get; set; }
    public string? Role { get; set; }
    public List<string>? ClassroomIds { get; set; }
    public bool? Enabled { get; set; }
}

public sealed class PasswordBody
{
    public string Password { get; set; } = string.Empty;
}

public sealed class KickBody
{
    public string? Reason { get; set; }
}

/// <summary>网站后台：设备注册/令牌、教室、账号、审计与系统状态。</summary>
public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin").RequireAuthorization("AdminOnly");

        group.MapGet("/overview", (DataStore store, ConnectionManager connections, CertificateInfo cert,
            ServerOptions options, ServiceInfo info) =>
        {
            var stats = store.Read(s => new
            {
                devices = s.Devices.Count,
                users = s.Users.Count,
                classrooms = s.Classrooms.Count,
                pending = s.Approvals.Count(a => a.Status == ApprovalStatus.Pending),
                otpAvailable = s.Otps.Count(o => !o.Revoked &&
                    o.UsedCount < Math.Max(1, o.MaxUses) &&
                    (o.ExpiresAt <= 0 || o.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
            });

            return Results.Ok(new
            {
                onlineDevices = connections.OnlineDeviceCount,
                totalDevices = stats.devices,
                users = stats.users,
                classrooms = stats.classrooms,
                pendingApprovals = stats.pending,
                availableOtp = stats.otpAvailable,
                httpsPort = options.HttpsPort,
                httpPort = options.HttpPort,
                dataDir = Path.Combine(AppContext.BaseDirectory, options.DataDir),
                startedAt = info.StartedAt,
                version = info.Version,
                certificate = new
                {
                    subject = cert.Subject,
                    thumbprint = cert.Thumbprint,
                    notAfter = cert.NotAfter,
                    selfSigned = cert.IsSelfSigned,
                    source = cert.Source,
                },
            });
        });

        // ---- 设备 ----

        group.MapGet("/devices", (DataStore store, ConnectionManager connections) =>
            Results.Ok(store.Read(s =>
            {
                var classrooms = s.Classrooms.ToDictionary(c => c.Id, c => c.Name);
                return s.Devices
                    .OrderBy(d => d.Name)
                    .Select(d => new
                    {
                        deviceId = d.DeviceId,
                        name = d.Name,
                        classroomId = d.ClassroomId,
                        classroomName = classrooms.TryGetValue(d.ClassroomId, out var name) ? name : d.ClassroomId,
                        enabled = d.Enabled,
                        bound = d.Bound,
                        online = connections.IsOnline(d.DeviceId),
                        tokenHint = d.TokenHint,
                        appVersion = d.AppVersion,
                        lastSeenAt = d.LastSeenAt,
                        lastIp = d.LastIp,
                        createdAt = d.CreatedAt,
                    })
                    .ToList();
            })));

        group.MapPost("/devices", (CreateDeviceBody body, HttpContext context, DataStore store, AuditService audit) =>
        {
            var name = (body.Name ?? string.Empty).Trim();
            var classroomId = (body.ClassroomId ?? string.Empty).Trim();

            if (name.Length == 0) return Results.BadRequest(new { error = "请填写设备名称" });
            if (classroomId.Length == 0) return Results.BadRequest(new { error = "请选择教室" });
            if (store.Read(s => s.Classrooms.All(c => c.Id != classroomId)))
                return Results.BadRequest(new { error = "教室不存在" });

            var deviceId = Guid.NewGuid().ToString("N");
            var token = Security.NewDeviceToken();

            var record = new DeviceRecord
            {
                DeviceId = deviceId,
                Name = name,
                ClassroomId = classroomId,
                TokenHash = Security.HashToken(token),
                TokenHint = token[..6] + "…",
            };

            store.Mutate(s => s.Devices.Add(record));

            var actor = AuthEndpoints.CurrentUser(context, store);
            audit.Write(actor?.Username ?? "-", Roles.Admin, "device.create", deviceId,
                $"创建设备 {name}", context.Connection.RemoteIpAddress?.ToString() ?? "");

            return Results.Ok(new
            {
                deviceId,
                token,
                tokenHint = record.TokenHint,
                message = "令牌只显示这一次，请填入一体机设置页并妥善保管",
            });
        });

        group.MapPost("/devices/{deviceId}/token", (string deviceId, HttpContext context, DataStore store, AuditService audit) =>
        {
            var token = Security.NewDeviceToken();
            var ok = store.Mutate(s =>
            {
                var device = s.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (device is null) return false;
                device.TokenHash = Security.HashToken(token);
                device.TokenHint = token[..6] + "…";
                device.Bound = false;   // 允许下一次连接重新绑定（换机/重装）
                return true;
            });

            if (!ok) return Results.NotFound(new { error = "设备不存在" });

            var actor = AuthEndpoints.CurrentUser(context, store);
            audit.Write(actor?.Username ?? "-", Roles.Admin, "device.token.reset", deviceId, "重置设备令牌",
                context.Connection.RemoteIpAddress?.ToString() ?? "");

            return Results.Ok(new { token, message = "旧令牌已立即失效，请更新一体机配置" });
        });

        group.MapPatch("/devices/{deviceId}", (string deviceId, PatchDeviceBody body, DataStore store) =>
        {
            var ok = store.Mutate(s =>
            {
                var device = s.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (device is null) return false;

                if (body.Name is not null && body.Name.Trim().Length > 0)
                    device.Name = body.Name.Trim();

                if (body.ClassroomId is not null && body.ClassroomId.Trim().Length > 0)
                {
                    var cid = body.ClassroomId.Trim();
                    if (s.Classrooms.All(c => c.Id != cid)) return false;
                    device.ClassroomId = cid;
                }

                if (body.Enabled is not null)
                    device.Enabled = body.Enabled.Value;

                return true;
            });

            return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "更新失败：设备或教室不存在" });
        });

        group.MapDelete("/devices/{deviceId}", (string deviceId, DataStore store) =>
        {
            var ok = store.Mutate(s =>
            {
                var device = s.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (device is null) return false;
                s.Devices.Remove(device);
                return true;
            });

            return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "设备不存在" });
        });

        group.MapPost("/devices/{deviceId}/kick", async (string deviceId, KickBody? body, HttpContext context,
            DataStore store, ConnectionManager connections, AuditService audit) =>
        {
            var device = store.Read(s => s.Devices.FirstOrDefault(d => d.DeviceId == deviceId));
            if (device is null) return Results.NotFound(new { error = "设备不存在" });

            var reason = string.IsNullOrWhiteSpace(body?.Reason) ? "管理员下线" : body!.Reason!;
            var sent = false;

            foreach (var conn in connections.GetConnections(deviceId).ToList())
            {
                try
                {
                    await connections.SendAsync(conn, Envelope.Create(Downstream.Kick, new KickPayload { Reason = reason }));
                    sent = true;
                }
                catch
                {
                    // 忽略
                }
            }

            var actor = AuthEndpoints.CurrentUser(context, store);
            audit.Write(actor?.Username ?? "-", Roles.Admin, "device.kick", deviceId, reason,
                context.Connection.RemoteIpAddress?.ToString() ?? "");

            return Results.Ok(new { ok = true, delivered = sent });
        });

        // ---- 教室 ----

        group.MapGet("/classrooms", (DataStore store, ConnectionManager connections) =>
            Results.Ok(store.Read(s => s.Classrooms.Select(c => new
            {
                c.Id,
                c.Name,
                c.Note,
                c.CreatedAt,
                deviceCount = s.Devices.Count(d => d.ClassroomId == c.Id),
                onlineCount = s.Devices.Where(d => d.ClassroomId == c.Id)
                    .Count(d => connections.IsOnline(d.DeviceId)),
            }).ToList())));

        group.MapPost("/classrooms", (CreateClassroomBody body, DataStore store) =>
        {
            var name = (body.Name ?? string.Empty).Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "请填写教室名称" });

            var record = new ClassroomRecord { Name = name, Note = body.Note };
            store.Mutate(s => s.Classrooms.Add(record));
            return Results.Ok(record);
        });

        group.MapDelete("/classrooms/{id}", (string id, DataStore store) =>
        {
            var error = store.Mutate(s =>
            {
                if (s.Devices.Any(d => d.ClassroomId == id)) return "该教室下还有设备，请先迁移或删除设备";
                var classroom = s.Classrooms.FirstOrDefault(c => c.Id == id);
                if (classroom is null) return "教室不存在";
                s.Classrooms.Remove(classroom);
                return null;
            });

            return error is null ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error });
        });

        // ---- 账号 ----

        group.MapGet("/users", (DataStore store) =>
            Results.Ok(store.Read(s => s.Users.Select(u => new
            {
                u.Id,
                u.Username,
                u.DisplayName,
                u.Role,
                u.ClassroomIds,
                u.Enabled,
                u.CreatedAt,
                u.LastLoginAt,
            }).ToList())));

        group.MapPost("/users", (CreateUserBody body, DataStore store) =>
        {
            var username = (body.Username ?? string.Empty).Trim();
            var password = body.Password ?? string.Empty;
            var role = body.Role == Roles.Admin ? Roles.Admin : Roles.Teacher;

            if (username.Length < 3) return Results.BadRequest(new { error = "用户名至少 3 位" });
            if (password.Length < 8) return Results.BadRequest(new { error = "密码至少 8 位" });
            if (store.Read(s => s.Users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))))
                return Results.BadRequest(new { error = "用户名已存在" });

            var (hash, salt) = Security.HashPassword(password);
            var user = new UserRecord
            {
                Username = username,
                DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? username : body.DisplayName.Trim(),
                Role = role,
                PasswordHash = hash,
                PasswordSalt = salt,
                ClassroomIds = role == Roles.Admin ? new List<string>() : (body.ClassroomIds ?? new List<string>()),
            };

            store.Mutate(s => s.Users.Add(user));
            return Results.Ok(new { user.Id, user.Username, user.DisplayName, user.Role });
        });

        group.MapPatch("/users/{id}", (string id, PatchUserBody body, HttpContext context, DataStore store) =>
        {
            var ok = store.Mutate(s =>
            {
                var user = s.Users.FirstOrDefault(u => u.Id == id);
                if (user is null) return false;

                if (body.DisplayName is not null && body.DisplayName.Trim().Length > 0)
                    user.DisplayName = body.DisplayName.Trim();

                if (body.Role is not null && (body.Role == Roles.Admin || body.Role == Roles.Teacher))
                    user.Role = body.Role;

                if (body.ClassroomIds is not null)
                    user.ClassroomIds = body.ClassroomIds.Distinct().ToList();

                if (body.Enabled is not null)
                    user.Enabled = body.Enabled.Value;

                return true;
            });

            return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "账号不存在" });
        });

        group.MapPost("/users/{id}/password", (string id, PasswordBody body, DataStore store) =>
        {
            if ((body.Password ?? string.Empty).Length < 8)
                return Results.BadRequest(new { error = "密码至少 8 位" });

            var (hash, salt) = Security.HashPassword(body.Password!);
            var ok = store.Mutate(s =>
            {
                var user = s.Users.FirstOrDefault(u => u.Id == id);
                if (user is null) return false;
                user.PasswordHash = hash;
                user.PasswordSalt = salt;
                return true;
            });

            return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "账号不存在" });
        });

        group.MapDelete("/users/{id}", (string id, HttpContext context, DataStore store) =>
        {
            var currentId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (currentId == id) return Results.BadRequest(new { error = "不能删除当前登录账号" });

            var ok = store.Mutate(s =>
            {
                var user = s.Users.FirstOrDefault(u => u.Id == id);
                if (user is null) return false;
                s.Users.Remove(user);
                return true;
            });

            return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "账号不存在" });
        });

        // ---- 审计 ----

        group.MapGet("/audit", (string? date, int? limit, AuditService audit) =>
        {
            var day = DateTime.Today;
            if (!string.IsNullOrWhiteSpace(date))
            {
                var normalized = date.Replace("-", "");
                if (DateTime.TryParseExact(normalized, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                    day = parsed;
            }

            return Results.Ok(audit.Read(day, Math.Clamp(limit ?? 200, 1, 1000)));
        });
    }
}
