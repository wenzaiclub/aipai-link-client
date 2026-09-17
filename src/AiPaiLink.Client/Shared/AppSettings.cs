using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppiieNet.Client.Services;

public sealed class AppSettings
{
    // 服务器地址内置在程序中，不向用户明文展示。
    // 开发者可用环境变量 APPIENET_API_URL 覆盖（方便测试不同后端）。
    public const string DefaultApiBaseUrl = "https://net.appiie.cn/api.php";

    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
    public string Username { get; set; } = "";
    public string Token { get; set; } = "";
    public string DeviceUid { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    public int? CurrentNetworkId { get; set; }
    public bool AutoStart { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public bool AutoConnectLast { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool ClosePrompt { get; set; } = true;
    public bool BroadcastRelay { get; set; }
    // 免密共享：队友不用输密码就能访问本机共享
    public bool SharePasswordless { get; set; }
    public string ShareUser { get; set; } = "aipai";
    public string SharePassword { get; set; } = "aipai2026";
    public string NodeId { get; set; } = "";
    public bool RememberPassword { get; set; }
    public string SavedUsername { get; set; } = "";
    public string SavedPassword { get; set; } = "";

    // 配置目录：艾派组网（旧版本用的 AppiieNet 目录会自动迁移过来）
    private static string Dir
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // 允许派生客户端（如紧凑版）用自己的目录，避免和主线客户端互相覆盖配置
            var overrideDir = Environment.GetEnvironmentVariable("APPIENET_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                root = overrideDir;
            }
            var dir = Path.Combine(root, "艾派组网");
            try
            {
                var legacy = Path.Combine(root, "AppiieNet", "settings.json");
                var cur = Path.Combine(dir, "settings.json");
                if (!File.Exists(cur) && File.Exists(legacy))
                {
                    Directory.CreateDirectory(dir);
                    File.Copy(legacy, cur, true);
                }
            }
            catch
            {
                // 迁移失败就按新目录走
            }
            return dir;
        }
    }

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (s != null)
                {
                    s.ApiBaseUrl =
                        Environment.GetEnvironmentVariable("APPIENET_API_URL") ?? DefaultApiBaseUrl;
                    if (string.IsNullOrEmpty(s.DeviceUid))
                    {
                        s.DeviceUid = Guid.NewGuid().ToString("N");
                    }
                    return s;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认
        }

        return new AppSettings
        {
            ApiBaseUrl =
                Environment.GetEnvironmentVariable("APPIENET_API_URL") ?? DefaultApiBaseUrl,
            DeviceUid = Guid.NewGuid().ToString("N"),
        };
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public void SaveCredentials(string username, string password)
    {
        RememberPassword = true;
        SavedUsername = username;
        SavedPassword = ProtectPassword(password);
        Save();
    }

    public (string Username, string Password) LoadCredentials()
    {
        if (!RememberPassword || string.IsNullOrEmpty(SavedUsername))
        {
            return ("", "");
        }
        return (SavedUsername, UnprotectPassword(SavedPassword));
    }

    public void ClearCredentials()
    {
        RememberPassword = false;
        SavedUsername = "";
        SavedPassword = "";
    }

    private static string ProtectPassword(string value)
    {
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null,
                DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(bytes);
        }
        catch
        {
            return "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }
    }

    private static string UnprotectPassword(string value)
    {
        try
        {
            if (value.StartsWith("dpapi:", StringComparison.Ordinal))
            {
                var bytes = Convert.FromBase64String(value.Substring(6));
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            }
            if (value.StartsWith("plain:", StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring(6)));
            }
        }
        catch
        {
            // 解密失败按空处理
        }
        return "";
    }
}
