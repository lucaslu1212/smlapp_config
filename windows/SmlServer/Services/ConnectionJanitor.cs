using SmlServer.Models;

namespace SmlServer.Services;

/// <summary>心跳巡检：超过配置时长没有心跳的连接强制关闭，教师端随即显示离线。</summary>
public sealed class ConnectionJanitor : BackgroundService
{
    private readonly ConnectionManager _connections;
    private readonly ServerOptions _options;
    private readonly ILogger<ConnectionJanitor> _logger;

    public ConnectionJanitor(ConnectionManager connections, ServerOptions options, ILogger<ConnectionJanitor> logger)
    {
        _connections = connections;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(30, _options.HeartbeatTimeoutSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

                var deadline = DateTimeOffset.UtcNow - timeout;
                foreach (var conn in _connections.AllConnections())
                {
                    if (!conn.IsOpen) continue;
                    if (conn.LastSeen >= deadline) continue;

                    _logger.LogWarning("心跳超时，断开设备 {DeviceId} conn={Conn}", conn.DeviceId, conn.ConnectionId);
                    await _connections.CloseAsync(conn, "心跳超时");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "心跳巡检异常");
            }
        }
    }
}
