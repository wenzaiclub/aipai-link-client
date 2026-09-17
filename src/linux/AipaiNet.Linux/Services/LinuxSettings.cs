using System.Text.Json;

namespace AppiieNet.Linux.Services;

/// <summary>Linux 客户端的本地配置（~/.config/aipai/config.json）</summary>
public sealed class LinuxSettings
{
    public const string DefaultApiBaseUrl = "https://net.appiie.cn/api.php";

    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
    public string Username { get; set; } = "";
    public string Token { get; set; } = "";
    public string DeviceUid { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int? LastNetworkId { get; set; }
    /// <summary>EasyTier 引擎目录（留空则自动查找）</summary>
    public string EngineDir { get; set; } = "";

    private static string Dir => Path.Combine(Home(), ".config", "aipai");
    private static string FilePath => Path.Combine(Dir, "config.json");

    private static string Home()
    {
        var h = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(h)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : h;
    }

    public static LinuxSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<LinuxSettings>(File.ReadAllText(FilePath), Opts);
                if (s != null)
                {
                    var env = Environment.GetEnvironmentVariable("APPIENET_API_URL");
                    s.ApiBaseUrl = string.IsNullOrEmpty(env) ? DefaultApiBaseUrl : env;
                    if (string.IsNullOrEmpty(s.DeviceUid))
                    {
                        s.DeviceUid = MakeDeviceUid();
                    }
                    if (string.IsNullOrEmpty(s.DeviceName))
                    {
                        s.DeviceName = Environment.MachineName;
                    }
                    s.ApplyEnvOverrides();
                    return s;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认
        }
        var env2 = Environment.GetEnvironmentVariable("APPIENET_API_URL");
        var fresh = new LinuxSettings
        {
            ApiBaseUrl = string.IsNullOrEmpty(env2) ? DefaultApiBaseUrl : env2,
            DeviceUid = MakeDeviceUid(),
            DeviceName = Environment.MachineName,
        };
        fresh.ApplyEnvOverrides();
        return fresh;
    }

    /// <summary>容器/服务场景用环境变量覆盖设置（不会写回配置文件）</summary>
    private void ApplyEnvOverrides()
    {
        var url = Environment.GetEnvironmentVariable("AIPAI_API_URL")
            ?? Environment.GetEnvironmentVariable("APPIENET_API_URL");
        if (!string.IsNullOrEmpty(url))
        {
            ApiBaseUrl = url;
        }
        var name = Environment.GetEnvironmentVariable("AIPAI_DEVICE_NAME");
        if (!string.IsNullOrEmpty(name))
        {
            DeviceName = name;
        }
        var dir = Environment.GetEnvironmentVariable("AIPAI_ENGINE_DIR")
            ?? Environment.GetEnvironmentVariable("APPIENET_ENGINE_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            EngineDir = dir;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                // 配置里有登录令牌，只给当前用户读写
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // 某些文件系统不支持，忽略
            }
        }
    }

    /// <summary>设备唯一标识：用 /etc/machine-id 生成，重装/重启后不变（固定IP 依赖它）</summary>
    private static string MakeDeviceUid()
    {
        foreach (var f in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.Exists(f))
                {
                    var id = File.ReadAllText(f).Trim();
                    if (id.Length >= 8)
                    {
                        return "linux-" + id;
                    }
                }
            }
            catch
            {
                // 忽略
            }
        }
        return "linux-" + Guid.NewGuid().ToString("N");
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
}
