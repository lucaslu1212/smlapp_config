using System.Text.Json;
using SmlServer.Models;

namespace SmlServer.Services;

/// <summary>
/// JSON 文件持久化（MVP 零依赖方案）。
/// 每个集合一个文件，写入用「临时文件 + 替换」避免断电写坏；所有读写串行化。
/// 数据规模是「一个学校几十到几百台设备」，JSON 足够；后续可平滑替换为 SQLite。
/// </summary>
public sealed class DataStore
{
    private readonly string _dir;
    private readonly BootstrapOptions _bootstrap;
    private readonly object _gate = new();

    public List<UserRecord> Users { get; private set; } = new();
    public List<ClassroomRecord> Classrooms { get; private set; } = new();
    public List<DeviceRecord> Devices { get; private set; } = new();
    public List<OtpRecord> Otps { get; private set; } = new();
    public List<OtpUsageRecord> OtpUsages { get; private set; } = new();
    public List<ApprovalRecord> Approvals { get; private set; } = new();

    public DataStore(string dataDir, BootstrapOptions bootstrap)
    {
        _dir = dataDir;
        _bootstrap = bootstrap;
    }

    public bool Load()
    {
        Directory.CreateDirectory(_dir);

        Users = ReadFile<List<UserRecord>>("users.json") ?? new();
        Classrooms = ReadFile<List<ClassroomRecord>>("classrooms.json") ?? new();
        Devices = ReadFile<List<DeviceRecord>>("devices.json") ?? new();
        Otps = ReadFile<List<OtpRecord>>("otps.json") ?? new();
        OtpUsages = ReadFile<List<OtpUsageRecord>>("otp-usages.json") ?? new();
        Approvals = ReadFile<List<ApprovalRecord>>("approvals.json") ?? new();

        var changed = false;
        var createdAdmin = false;

        if (Users.Count == 0)
        {
            var (hash, salt) = Security.HashPassword(_bootstrap.AdminPassword);
            Users.Add(new UserRecord
            {
                Username = _bootstrap.AdminUsername,
                DisplayName = "系统管理员",
                Role = Roles.Admin,
                PasswordHash = hash,
                PasswordSalt = salt,
            });
            createdAdmin = true;
            changed = true;
        }

        if (Classrooms.Count == 0)
        {
            Classrooms.Add(new ClassroomRecord { Id = "cls-default", Name = "默认教室", Note = "首次启动自动创建，可在后台改名" });
            changed = true;
        }

        if (changed) SaveNoLock();
        return createdAdmin;
    }

    public T Read<T>(Func<DataStore, T> action)
    {
        lock (_gate) return action(this);
    }

    public T Mutate<T>(Func<DataStore, T> action)
    {
        lock (_gate)
        {
            var result = action(this);
            SaveNoLock();
            return result;
        }
    }

    public void Mutate(Action<DataStore> action)
    {
        lock (_gate)
        {
            action(this);
            SaveNoLock();
        }
    }

    private void SaveNoLock()
    {
        WriteFile("users.json", Users);
        WriteFile("classrooms.json", Classrooms);
        WriteFile("devices.json", Devices);
        WriteFile("otps.json", Otps);
        WriteFile("otp-usages.json", OtpUsages);
        WriteFile("approvals.json", Approvals);
    }

    private T? ReadFile<T>(string name) where T : class
    {
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path)) return null;

        try
        {
            return Json.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            var backup = path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try { File.Move(path, backup); } catch { }
            Console.Error.WriteLine($"[store] {name} 解析失败，已备份为 {backup}: {ex.Message}");
            return null;
        }
    }

    private void WriteFile<T>(string name, T value)
    {
        try
        {
            var path = Path.Combine(_dir, name);
            var temp = path + ".tmp";
            File.WriteAllText(temp, Json.Serialize(value));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[store] 写入 {name} 失败: {ex.Message}");
        }
    }
}
