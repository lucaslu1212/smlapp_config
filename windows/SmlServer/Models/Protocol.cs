using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmlServer.Models;

/// <summary>全局 JSON 配置：camelCase，与 Windows 客户端、小程序保持一致。</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    public static JsonElement? ToElement(object value) => JsonSerializer.SerializeToElement(value, Options);
}

/// <summary>消息信封（API.md §3）。</summary>
public sealed class Envelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("replyTo")] public string? ReplyTo { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("data")] public JsonElement? Data { get; set; }

    public T? Read<T>() where T : class
    {
        if (Data is not { } el) return null;
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        try { return el.Deserialize<T>(Json.Options); }
        catch (JsonException) { return null; }
    }

    public static Envelope Create(string type, object? data = null, string? id = null, string? replyTo = null)
        => new()
        {
            Type = type,
            Id = id ?? Guid.NewGuid().ToString("N")[..12],
            ReplyTo = replyTo,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Data = data is null ? null : JsonSerializer.SerializeToElement(data, Json.Options),
        };

    public static Envelope Parse(string json)
        => JsonSerializer.Deserialize<Envelope>(json, Json.Options)
           ?? throw new JsonException("消息不是合法的信封结构");
}

/// <summary>客户端 → 服务器。</summary>
public static class Upstream
{
    public const string Hello = "hello";
    public const string Heartbeat = "heartbeat";
    public const string Ack = "ack";
    public const string Event = "event";
    public const string ScreenResult = "screen.result";
    public const string PolicyState = "policy.state";
    public const string StudentMessage = "message.student";
    public const string AiVerdict = "ai.verdict";
    public const string CallTeacher = "student.call.teacher";
    public const string ControlRequest = "device.control.request";
    public const string OtpPull = "otp.pull";
    public const string OtpReport = "otp.report";
    public const string UninstallRequest = "uninstall.request";
    public const string ExitRequest = "exit.request";
}

/// <summary>服务器 → 客户端。</summary>
public static class Downstream
{
    public const string CallStudent = "call.student";
    public const string ShowMessage = "message.show";
    public const string PowerLock = "pc.lock";
    public const string PowerShutdown = "pc.shutdown";
    public const string PowerRestart = "pc.restart";
    public const string Volume = "pc.volume";
    public const string Screen = "pc.screen";
    public const string PolicyUpdate = "policy.update";
    public const string Kick = "client.kick";
    public const string Ping = "ping";
    public const string OtpSync = "otp.sync";
    public const string UninstallGrant = "uninstall.grant";
    public const string UninstallDeny = "uninstall.deny";
    public const string ExitGrant = "exit.grant";
    public const string ExitDeny = "exit.deny";
}

// ============ 客户端 → 服务器 ============

public sealed class HelloPayload
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("classroom")] public string Classroom { get; set; } = string.Empty;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonPropertyName("osVersion")] public string OsVersion { get; set; } = string.Empty;
    [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;
    [JsonPropertyName("ip")] public string Ip { get; set; } = string.Empty;
    [JsonPropertyName("mac")] public string Mac { get; set; } = string.Empty;
}

public sealed class HeartbeatPayload
{
    [JsonPropertyName("uptimeSeconds")] public int UptimeSeconds { get; set; }
    [JsonPropertyName("proxyRunning")] public bool ProxyRunning { get; set; }
    [JsonPropertyName("appGuardRunning")] public bool AppGuardRunning { get; set; }
    [JsonPropertyName("tunRunning")] public bool TunRunning { get; set; }
    [JsonPropertyName("activeWindow")] public string? ActiveWindow { get; set; }
}

public sealed class AckPayload
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

public sealed class OtpCodePayload
{
    [JsonPropertyName("otpId")] public string OtpId { get; set; } = string.Empty;
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("issuedBy")] public string IssuedBy { get; set; } = string.Empty;
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("maxUses")] public int MaxUses { get; set; } = 1;
}

public sealed class OtpSyncPayload
{
    [JsonPropertyName("replace")] public bool Replace { get; set; }
    [JsonPropertyName("codes")] public List<OtpCodePayload> Codes { get; set; } = new();
}

public sealed class OtpUsagePayload
{
    [JsonPropertyName("usageId")] public string UsageId { get; set; } = string.Empty;
    [JsonPropertyName("otpId")] public string OtpId { get; set; } = string.Empty;
    [JsonPropertyName("usedAt")] public long UsedAt { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("result")] public string Result { get; set; } = "success";
    [JsonPropertyName("attemptHash")] public string? AttemptHash { get; set; }
}

public sealed class OtpReportPayload
{
    [JsonPropertyName("usages")] public List<OtpUsagePayload> Usages { get; set; } = new();
}

public sealed class UninstallRequestPayload
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = "设备端发起卸载申请";
}

public sealed class ExitRequestPayload
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = "设备端发起退出申请";
}

public sealed class ScreenResultPayload
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("imageBase64")] public string? ImageBase64 { get; set; }
    [JsonPropertyName("capturedAt")] public long CapturedAt { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

// ============ 服务器 → 客户端 ============

public sealed class PolicyUpdatePayload
{
    [JsonPropertyName("app")] public AppPolicy? App { get; set; }
    [JsonPropertyName("network")] public NetworkPolicy? Network { get; set; }
}

public sealed class CallStudentPayload
{
    [JsonPropertyName("text")] public string Text { get; set; } = "请到讲台来一下";
    [JsonPropertyName("teacherName")] public string? TeacherName { get; set; }
    [JsonPropertyName("teacherId")] public string? TeacherId { get; set; }
    [JsonPropertyName("durationSeconds")] public int DurationSeconds { get; set; } = 30;
    [JsonPropertyName("playSound")] public bool PlaySound { get; set; } = true;
    [JsonPropertyName("speak")] public bool Speak { get; set; } = true;
}

public sealed class ShowMessagePayload
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("level")] public string Level { get; set; } = "info";
    [JsonPropertyName("durationSeconds")] public int DurationSeconds { get; set; }
}

public sealed class PowerPayload
{
    [JsonPropertyName("delaySeconds")] public int DelaySeconds { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class VolumePayload
{
    [JsonPropertyName("percent")] public int Percent { get; set; }
}

public sealed class ScreenRequestPayload
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("maxWidth")] public int MaxWidth { get; set; } = 1600;
    [JsonPropertyName("quality")] public int Quality { get; set; } = 70;
}

public sealed class KickPayload
{
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class UninstallGrantPayload
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("teacherId")] public string? TeacherId { get; set; }
    [JsonPropertyName("teacherName")] public string? TeacherName { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
}

public sealed class UninstallDenyPayload
{
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class ExitGrantPayload
{
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("teacherId")] public string? TeacherId { get; set; }
    [JsonPropertyName("teacherName")] public string? TeacherName { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
}

public sealed class ExitDenyPayload
{
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}
