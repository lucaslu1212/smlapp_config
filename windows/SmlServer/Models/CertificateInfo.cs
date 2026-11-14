namespace SmlServer.Models;

/// <summary>当前 HTTPS 证书信息（后台展示用；不含私钥）。</summary>
public sealed class CertificateInfo
{
    public string Subject { get; init; } = string.Empty;
    public string Thumbprint { get; init; } = string.Empty;
    public DateTime NotAfter { get; init; }
    public DateTime NotBefore { get; init; }
    public bool IsSelfSigned { get; init; }

    /// <summary>pfx（配置）或 self-signed（自动生成）。</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>服务启动时间等运行时信息。</summary>
public sealed class ServiceInfo
{
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public string Version { get; } = typeof(ServiceInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
