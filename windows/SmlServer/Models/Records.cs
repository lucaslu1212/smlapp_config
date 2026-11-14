using System.Text.Json.Serialization;

namespace SmlServer.Models;

public static class Roles
{
    public const string Teacher = "teacher";
    public const string Admin = "admin";
}

public static class ApprovalKind
{
    public const string Uninstall = "uninstall";
    public const string Exit = "exit";
}

public static class ApprovalStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Expired = "expired";
}

/// <summary>教师/管理员账号。密码只存 PBKDF2 哈希与盐。</summary>
public sealed class UserRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;

    /// <summary>teacher | admin</summary>
    public string Role { get; set; } = Roles.Teacher;

    /// <summary>任教班级；admin 忽略。教师只能看到这些教室的设备。</summary>
    public List<string> ClassroomIds { get; set; } = new();

    public bool Enabled { get; set; } = true;
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long LastLoginAt { get; set; }
}

public sealed class ClassroomRecord
{
    public string Id { get; set; } = "cls-" + Guid.NewGuid().ToString("N")[..6];
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>受管设备。token 只存 SHA-256，明文仅在创建/重置时返回一次。</summary>
public sealed class DeviceRecord
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ClassroomId { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>令牌提示（前 6 位），方便教师核对，不足以还原令牌。</summary>
    public string TokenHint { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>是否已完成首次绑定。后台创建设备时为 false，设备首次用令牌连接后绑定其真实 deviceId。</summary>
    public bool Bound { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long LastSeenAt { get; set; }
    public string LastIp { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string MachineUser { get; set; } = string.Empty;
    public string ClassroomLabel { get; set; } = string.Empty;

    /// <summary>最近一次心跳里的运行状态。</summary>
    public DeviceRuntime? Runtime { get; set; }

    /// <summary>下发给该设备的策略（教师配置后保存）。</summary>
    public StoredPolicy Policy { get; set; } = new();
}

public sealed class DeviceRuntime
{
    public int UptimeSeconds { get; set; }
    public bool ProxyRunning { get; set; }
    public bool AppGuardRunning { get; set; }
    public bool TunRunning { get; set; }
    public long At { get; set; }
}

public sealed class StoredPolicy
{
    public AppPolicy? App { get; set; }
    public NetworkPolicy? Network { get; set; }
}

public enum FilterMode
{
    Off = 0,
    Blacklist = 1,
    Whitelist = 2,
}

public sealed class AppPolicy
{
    [JsonPropertyName("mode")] public FilterMode Mode { get; set; } = FilterMode.Off;
    [JsonPropertyName("blacklist")] public List<string> Blacklist { get; set; } = new();
    [JsonPropertyName("whitelist")] public List<string> Whitelist { get; set; } = new();
    [JsonPropertyName("scanIntervalSeconds")] public int ScanIntervalSeconds { get; set; } = 2;
    [JsonPropertyName("allowSystemProcesses")] public bool AllowSystemProcesses { get; set; } = true;
}

public sealed class NetworkPolicy
{
    [JsonPropertyName("mode")] public FilterMode Mode { get; set; } = FilterMode.Off;
    [JsonPropertyName("domainBlacklist")] public List<string> DomainBlacklist { get; set; } = new();
    [JsonPropertyName("domainWhitelist")] public List<string> DomainWhitelist { get; set; } = new();
    [JsonPropertyName("ipBlacklist")] public List<string> IpBlacklist { get; set; } = new();
    [JsonPropertyName("ipWhitelist")] public List<string> IpWhitelist { get; set; } = new();
    [JsonPropertyName("showBlockPage")] public bool ShowBlockPage { get; set; } = true;
}

/// <summary>一次性密码。只存归一化明文的 SHA-256。</summary>
public sealed class OtpRecord
{
    public string OtpId { get; set; } = "otp_" + Guid.NewGuid().ToString("N")[..10];
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>归属教室；为空表示不限教室（仅管理员可生成）。</summary>
    public string ClassroomId { get; set; } = string.Empty;

    /// <summary>限定单台设备；为空表示教室内的设备都可用。</summary>
    public string DeviceId { get; set; } = string.Empty;

    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; }
    public long ExpiresAt { get; set; }
    public string? Note { get; set; }
    public string IssuedBy { get; set; } = string.Empty;
    public long IssuedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool Revoked { get; set; }
    public long RevokedAt { get; set; }
}

public sealed class OtpUsageRecord
{
    public string UsageId { get; set; } = string.Empty;
    public string OtpId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;

    /// <summary>success | failed</summary>
    public string Result { get; set; } = "success";

    public string? AttemptHash { get; set; }
    public long At { get; set; }
}

/// <summary>卸载/退出审批。设备发起 request 后由教师端批准或拒绝。</summary>
public sealed class ApprovalRecord
{
    public string RequestId { get; set; } = string.Empty;
    public string Kind { get; set; } = ApprovalKind.Exit;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public long RequestedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long ExpiresAt { get; set; }

    /// <summary>pending | approved | denied | expired</summary>
    public string Status { get; set; } = ApprovalStatus.Pending;

    public string DecidedBy { get; set; } = string.Empty;
    public long DecidedAt { get; set; }
    public string? DecisionNote { get; set; }

    [JsonIgnore] public bool IsPending => Status == ApprovalStatus.Pending;
}

public sealed class AuditEntry
{
    public long At { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Actor { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string Ip { get; set; } = string.Empty;
}

/// <summary>设备事件（拦截、AI 判定、心跳异常等），内存环形缓冲 + 落盘采样。</summary>
public sealed class DeviceEvent
{
    public long At { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string DeviceId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Raw { get; set; } = string.Empty;
}

/// <summary>服务端配置（appsettings 的 Server 节）。</summary>
public sealed class ServerOptions
{
    public int HttpsPort { get; set; } = 443;
    public int HttpPort { get; set; } = 80;
    public string DataDir { get; set; } = "data";
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;
    public int HeartbeatTimeoutSeconds { get; set; } = 90;
    public string SelfSignedCertificateName { get; set; } = "smlapp-server.pfx";
}

public sealed class BootstrapOptions
{
    public string AdminUsername { get; set; } = "admin";
    public string AdminPassword { get; set; } = "Admin@12345";
}
