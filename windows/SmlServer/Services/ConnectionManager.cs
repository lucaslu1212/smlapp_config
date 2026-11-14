using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using SmlServer.Models;

namespace SmlServer.Services;

/// <summary>一条设备连接。同一 deviceId 可有多条（主连接 + 卸载/退出审批连接）。</summary>
public sealed class DeviceConnection
{
    public string ConnectionId { get; } = Guid.NewGuid().ToString("N")[..8];
    public string DeviceId { get; init; } = string.Empty;
    public WebSocket Socket { get; init; } = null!;
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public string RemoteIp { get; init; } = string.Empty;

    /// <summary>连接绑定到某个审批请求（卸载/退出）；普通指令不会发到这种连接。</summary>
    public string? ApprovalRequestId { get; set; }

    public DeviceRuntime? Runtime { get; set; }

    /// <summary>等待 ack 的指令：key = 下行消息 id。</summary>
    public ConcurrentDictionary<string, TaskCompletionSource<AckPayload>> Pending { get; } = new();

    public bool IsOpen => Socket.State == WebSocketState.Open;
}

public sealed class CommandResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// 连接注册表。
///
/// 与 API.md 的「同设备重复连接踢旧连接」略有不同：为了不把卸载/退出审批通道挤掉，
/// 这里允许同 deviceId 并存多条连接。普通指令发往最新的一条；grant/deny 发往发起申请的连接。
/// </summary>
public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DeviceConnection>> _byDevice = new();

    public DeviceConnection Register(string deviceId, WebSocket socket, string remoteIp)
    {
        var conn = new DeviceConnection { DeviceId = deviceId, Socket = socket, RemoteIp = remoteIp };
        var set = _byDevice.GetOrAdd(deviceId, _ => new ConcurrentDictionary<string, DeviceConnection>());
        set[conn.ConnectionId] = conn;
        return conn;
    }

    public void Unregister(DeviceConnection conn)
    {
        if (_byDevice.TryGetValue(conn.DeviceId, out var set))
        {
            set.TryRemove(conn.ConnectionId, out _);
            if (set.IsEmpty) _byDevice.TryRemove(conn.DeviceId, out _);
        }

        foreach (var key in conn.Pending.Keys)
        {
            if (conn.Pending.TryRemove(key, out var tcs))
                tcs.TrySetResult(new AckPayload { Ok = false, Error = "连接已断开" });
        }
    }

    public IReadOnlyList<DeviceConnection> GetConnections(string deviceId)
        => _byDevice.TryGetValue(deviceId, out var set)
            ? set.Values.ToList()
            : (IReadOnlyList<DeviceConnection>)Array.Empty<DeviceConnection>();

    /// <summary>普通指令的目标连接：优先最新建立的主连接（排除审批通道）。</summary>
    public DeviceConnection? GetPrimary(string deviceId)
        => GetConnections(deviceId)
            .Where(c => c.ApprovalRequestId is null && c.IsOpen)
            .OrderByDescending(c => c.ConnectedAt)
            .FirstOrDefault()
           ?? GetConnections(deviceId).FirstOrDefault(c => c.IsOpen);

    public DeviceConnection? FindByRequestId(string requestId)
        => _byDevice.Values
            .SelectMany(s => s.Values)
            .FirstOrDefault(c => c.ApprovalRequestId == requestId && c.IsOpen);

    public bool IsOnline(string deviceId)
        => GetConnections(deviceId).Any(c => c.IsOpen);

    public int OnlineDeviceCount
        => _byDevice.Values.Count(s => s.Values.Any(c => c.IsOpen));

    public IEnumerable<(string DeviceId, DeviceRuntime? Runtime, DateTimeOffset LastSeen)> Snapshot()
        => _byDevice.Select(kv =>
        {
            var conn = kv.Value.Values.OrderByDescending(c => c.LastSeen).FirstOrDefault();
            return (kv.Key, conn?.Runtime, conn?.LastSeen ?? DateTimeOffset.MinValue);
        });

    public IEnumerable<DeviceConnection> AllConnections()
        => _byDevice.Values.SelectMany(s => s.Values);

    public async Task SendAsync(DeviceConnection conn, Envelope envelope, CancellationToken ct = default)
    {
        if (!conn.IsOpen) throw new InvalidOperationException("连接已关闭");

        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(envelope));
        if (bytes.Length > 4 * 1024 * 1024)
            throw new InvalidOperationException("消息超过 4 MiB 限制");

        await conn.SendLock.WaitAsync(ct);
        try
        {
            if (!conn.IsOpen) throw new InvalidOperationException("连接已关闭");
            await conn.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            conn.SendLock.Release();
        }
    }

    /// <summary>向设备下发指令并等待 ack（replyTo 匹配），超时视为失败。</summary>
    public async Task<CommandResult> SendCommandAsync(
        string deviceId, Envelope envelope, TimeSpan timeout, CancellationToken ct = default)
    {
        var conn = GetPrimary(deviceId);
        if (conn is null)
            return new CommandResult { Ok = false, Error = "设备不在线" };

        var tcs = new TaskCompletionSource<AckPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Pending[envelope.Id!] = tcs;

        try
        {
            await SendAsync(conn, envelope, ct);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
            if (completed != tcs.Task)
                return new CommandResult { Ok = false, Error = "等待设备回执超时" };

            var ack = await tcs.Task;
            return new CommandResult { Ok = ack.Ok, Error = ack.Error, Note = ack.Note };
        }
        catch (Exception ex)
        {
            return new CommandResult { Ok = false, Error = ex.Message };
        }
        finally
        {
            conn.Pending.TryRemove(envelope.Id!, out _);
        }
    }

    /// <summary>审批结论：优先发给发起申请的连接，其次主连接。</summary>
    public async Task<bool> SendToApprovalChannelAsync(string requestId, string deviceId, Envelope envelope)
    {
        var conn = FindByRequestId(requestId) ?? GetPrimary(deviceId);
        if (conn is null) return false;

        try
        {
            await SendAsync(conn, envelope);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task CloseAsync(DeviceConnection conn, string reason)
    {
        try
        {
            if (conn.IsOpen)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await conn.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token);
            }
        }
        catch
        {
            // 忽略关闭异常
        }
        finally
        {
            try { conn.Socket.Dispose(); } catch { }
            Unregister(conn);
        }
    }
}
