using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SmlServer.Models;
using SmlServer.Services;

namespace SmlServer.Endpoints;

/// <summary>
/// 设备长连接 `/ws/agent`（API.md §2、§4、§5）。
///
/// 鉴权：`Authorization: Bearer &lt;设备令牌&gt;` + `X-Device-Id`，令牌必须与设备绑定。
/// 同一设备允许多条连接：主连接负责指令，卸载/退出审批连接通过 ApprovalRequestId 定向收结论。
/// </summary>
public static class AgentEndpoint
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    public static async Task HandleAsync(
        HttpContext context,
        DataStore store,
        ConnectionManager connections,
        OtpService otp,
        ApprovalService approvals,
        DeviceEventService events,
        AuditService audit,
        ServerOptions options,
        ILogger logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket required");
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var deviceId = context.Request.Headers["X-Device-Id"].ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(deviceId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("missing credentials");
            return;
        }

        var token = authorization["Bearer ".Length..].Trim();
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

        var device = store.Read(s => s.Devices.FirstOrDefault(d => d.DeviceId == deviceId));
        var authorized = device is not null && device.Enabled && Security.TokenMatches(token, device.TokenHash);

        if (!authorized)
        {
            // 首次接入：后台创建的是「注册令牌」，此时用设备自己的 deviceId 完成绑定。
            var bound = store.Mutate(s =>
            {
                var candidate = s.Devices.FirstOrDefault(d =>
                    !d.Bound && d.Enabled && Security.TokenMatches(token, d.TokenHash));
                if (candidate is null) return null;

                // 同一台设备重新绑定：清掉旧的同 ID 记录，避免主键冲突
                var duplicate = s.Devices.FirstOrDefault(d => d.DeviceId == deviceId && !ReferenceEquals(d, candidate));
                if (duplicate is not null) s.Devices.Remove(duplicate);

                candidate.DeviceId = deviceId;
                candidate.Bound = true;
                candidate.LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                candidate.LastIp = remoteIp;
                return candidate;
            });

            if (bound is null)
            {
                logger.LogWarning("设备鉴权失败 deviceId={DeviceId} ip={Ip}", deviceId, remoteIp);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("invalid token");
                return;
            }

            device = bound;
            logger.LogInformation("设备首次接入并绑定：{DeviceId} ({Name})", deviceId, device.Name);
        }

        if (device is null || !device.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("device disabled");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var conn = connections.Register(deviceId, socket, remoteIp);

        logger.LogInformation("设备上线 {DeviceId} ({Name}) ip={Ip} conn={Conn}",
            deviceId, device.Name, remoteIp, conn.ConnectionId);

        TouchDevice(store, deviceId, remoteIp);

        try
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();

            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "text only", CancellationToken.None);
                    break;
                }

                if (message.Length + result.Count > MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None);
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                conn.LastSeen = DateTimeOffset.UtcNow;

                Envelope envelope;
                try { envelope = Envelope.Parse(json); }
                catch (JsonException ex)
                {
                    logger.LogWarning("忽略格式错误的设备消息: {Error}", ex.Message);
                    continue;
                }

                await HandleMessageAsync(envelope, conn, context, store, connections, otp, approvals, events, audit, options, logger);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常断开
        }
        catch (WebSocketException ex)
        {
            logger.LogInformation("设备连接中断 {DeviceId}: {Error}", deviceId, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "设备连接异常 {DeviceId}", deviceId);
        }
        finally
        {
            connections.Unregister(conn);
            TouchDevice(store, deviceId, remoteIp);
            logger.LogInformation("设备下线 {DeviceId} conn={Conn}", deviceId, conn.ConnectionId);
        }
    }

    private static async Task HandleMessageAsync(
        Envelope envelope,
        DeviceConnection conn,
        HttpContext context,
        DataStore store,
        ConnectionManager connections,
        OtpService otp,
        ApprovalService approvals,
        DeviceEventService events,
        AuditService audit,
        ServerOptions options,
        ILogger logger)
    {
        switch (envelope.Type)
        {
            case Upstream.Hello:
            {
                var hello = envelope.Read<HelloPayload>();
                if (hello is not null)
                {
                    store.Mutate(s =>
                    {
                        var device = s.Devices.FirstOrDefault(d => d.DeviceId == conn.DeviceId);
                        if (device is null) return;
                        if (!string.IsNullOrWhiteSpace(hello.DeviceName)) device.Name = hello.DeviceName.Trim();
                        if (!string.IsNullOrWhiteSpace(hello.Classroom)) device.ClassroomLabel = hello.Classroom.Trim();
                        device.AppVersion = hello.AppVersion;
                        device.OsVersion = hello.OsVersion;
                        device.MachineUser = hello.UserName;
                        device.LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        device.LastIp = conn.RemoteIp;
                    });

                    // 初始策略同步（API.md §11：hello 之后可回一条 policy.update）
                    var policy = store.Read(s => s.Devices.FirstOrDefault(d => d.DeviceId == conn.DeviceId)?.Policy);
                    if (policy is not null && (policy.App is not null || policy.Network is not null))
                    {
                        await connections.SendAsync(conn, Envelope.Create(Downstream.PolicyUpdate,
                            new PolicyUpdatePayload { App = policy.App, Network = policy.Network }));
                    }
                }
                break;
            }

            case Upstream.Heartbeat:
            {
                var heartbeat = envelope.Read<HeartbeatPayload>();
                if (heartbeat is not null)
                {
                    conn.Runtime = new DeviceRuntime
                    {
                        UptimeSeconds = heartbeat.UptimeSeconds,
                        ProxyRunning = heartbeat.ProxyRunning,
                        AppGuardRunning = heartbeat.AppGuardRunning,
                        TunRunning = heartbeat.TunRunning,
                        At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                }
                break;
            }

            case Upstream.Ack:
            {
                if (!string.IsNullOrEmpty(envelope.ReplyTo) &&
                    conn.Pending.TryRemove(envelope.ReplyTo, out var pending))
                {
                    pending.TrySetResult(envelope.Read<AckPayload>() ?? new AckPayload { Ok = true });
                }
                break;
            }

            case Upstream.OtpPull:
            {
                var device = store.Read(s => s.Devices.FirstOrDefault(d => d.DeviceId == conn.DeviceId));
                var sync = otp.BuildSync(conn.DeviceId, device?.ClassroomId ?? string.Empty);
                await connections.SendAsync(conn, Envelope.Create(Downstream.OtpSync, sync));
                break;
            }

            case Upstream.OtpReport:
            {
                var report = envelope.Read<OtpReportPayload>();
                if (report is not null)
                    otp.HandleReport(conn.DeviceId, report);
                break;
            }

            case Upstream.UninstallRequest:
            {
                var request = envelope.Read<UninstallRequestPayload>() ?? new UninstallRequestPayload();
                if (string.IsNullOrEmpty(envelope.Id))
                {
                    logger.LogWarning("卸载申请缺少消息 id，device={DeviceId}", conn.DeviceId);
                    break;
                }

                conn.ApprovalRequestId = envelope.Id;
                approvals.Create(ApprovalKind.Uninstall, conn.DeviceId,
                    string.IsNullOrWhiteSpace(request.DeviceName) ? conn.DeviceId : request.DeviceName,
                    request.Reason, envelope.Id);
                break;
            }

            case Upstream.ExitRequest:
            {
                var request = envelope.Read<ExitRequestPayload>() ?? new ExitRequestPayload();
                if (string.IsNullOrEmpty(envelope.Id))
                {
                    logger.LogWarning("退出申请缺少消息 id，device={DeviceId}", conn.DeviceId);
                    break;
                }

                conn.ApprovalRequestId = envelope.Id;
                approvals.Create(ApprovalKind.Exit, conn.DeviceId,
                    string.IsNullOrWhiteSpace(request.DeviceName) ? conn.DeviceId : request.DeviceName,
                    request.Reason, envelope.Id);
                break;
            }

            case Upstream.ScreenResult:
            {
                var result = envelope.Read<ScreenResultPayload>();
                if (result is not null)
                    SaveScreenshot(result, options, logger);
                break;
            }

            case Upstream.Event:
            case Upstream.AiVerdict:
            case Upstream.CallTeacher:
            case Upstream.StudentMessage:
            case Upstream.ControlRequest:
            case Upstream.PolicyState:
            {
                events.Add(new DeviceEvent
                {
                    DeviceId = conn.DeviceId,
                    Type = envelope.Type,
                    Kind = TryGetString(envelope, "kind"),
                    Message = TryGetString(envelope, "message")
                              ?? TryGetString(envelope, "reason")
                              ?? TryGetString(envelope, "domain")
                              ?? string.Empty,
                    Raw = envelope.Data is { } data ? data.GetRawText() : string.Empty,
                });
                break;
            }

            default:
                // 未知类型只记录，**不回 ack**：客户端对未知下行会再回 ack，可能形成回环。
                logger.LogDebug("未知设备消息类型 {Type}", envelope.Type);
                break;
        }
    }

    private static string? TryGetString(Envelope envelope, string property)
    {
        if (envelope.Data is not { ValueKind: JsonValueKind.Object } data) return null;
        return data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static void TouchDevice(DataStore store, string deviceId, string ip)
    {
        store.Mutate(s =>
        {
            var device = s.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (device is null) return;
            device.LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!string.IsNullOrWhiteSpace(ip)) device.LastIp = ip;
        });
    }

    private static void SaveScreenshot(ScreenResultPayload result, ServerOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(result.ImageBase64)) return;

        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, options.DataDir, "screens");
            Directory.CreateDirectory(dir);
            var bytes = Convert.FromBase64String(result.ImageBase64);
            var path = Path.Combine(dir, Sanitize(result.RequestId) + ".jpg");
            File.WriteAllBytes(path, bytes);
            logger.LogInformation("截屏已保存 {Path} ({Size} bytes)", path, bytes.Length);
        }
        catch (Exception ex)
        {
            logger.LogWarning("截屏保存失败: {Error}", ex.Message);
        }
    }

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value;
    }
}
