using System.Collections.Concurrent;
using System.Text;
using SmlServer.Models;

namespace SmlServer.Services;

/// <summary>设备事件：内存保留最近 2000 条，同时按天追加 JSONL，供教师端查看与事后审计。</summary>
public sealed class DeviceEventService
{
    private const int Capacity = 2000;
    private readonly ConcurrentQueue<DeviceEvent> _events = new();
    private readonly string _dir;
    private readonly object _gate = new();

    public DeviceEventService(string dataDir)
    {
        _dir = Path.Combine(dataDir, "events");
        Directory.CreateDirectory(_dir);
    }

    public void Add(DeviceEvent evt)
    {
        _events.Enqueue(evt);
        while (_events.Count > Capacity) _events.TryDequeue(out _);

        try
        {
            lock (_gate)
            {
                var path = Path.Combine(_dir, $"events-{DateTime.Now:yyyyMMdd}.jsonl");
                File.AppendAllText(path, Json.Serialize(evt) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 事件落盘失败不影响连接
        }
    }

    public List<DeviceEvent> Recent(string? deviceId, int limit)
    {
        return _events
            .Where(e => string.IsNullOrEmpty(deviceId) || e.DeviceId == deviceId)
            .OrderByDescending(e => e.At)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
    }
}
