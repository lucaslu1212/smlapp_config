using System.Text;
using SmlServer.Models;

namespace SmlServer.Services;

/// <summary>审计日志：按天追加 JSONL，只落盘不阻塞主流程。</summary>
public sealed class AuditService
{
    private readonly string _dir;
    private readonly object _gate = new();

    public AuditService(string dataDir)
    {
        _dir = Path.Combine(dataDir, "audit");
        Directory.CreateDirectory(_dir);
    }

    public void Write(string actor, string role, string action, string target, string? detail, string ip)
    {
        var entry = new AuditEntry
        {
            Actor = actor,
            Role = role,
            Action = action,
            Target = target,
            Detail = detail,
            Ip = ip,
        };

        try
        {
            lock (_gate)
            {
                var path = Path.Combine(_dir, $"audit-{DateTime.Now:yyyyMMdd}.jsonl");
                File.AppendAllText(path, Json.Serialize(entry) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[audit] 写入失败: " + ex.Message);
        }
    }

    /// <summary>读取某天的审计（倒序，最多 limit 条）。</summary>
    public List<AuditEntry> Read(DateTime date, int limit)
    {
        var path = Path.Combine(_dir, $"audit-{date:yyyyMMdd}.jsonl");
        if (!File.Exists(path)) return new List<AuditEntry>();

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var result = new List<AuditEntry>();
        for (var i = lines.Length - 1; i >= 0 && result.Count < limit; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            try
            {
                var entry = Json.Deserialize<AuditEntry>(lines[i]);
                if (entry is not null) result.Add(entry);
            }
            catch
            {
                // 跳过坏行
            }
        }
        return result;
    }
}
