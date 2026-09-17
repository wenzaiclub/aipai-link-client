using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppiieNet.Client.Services;

public sealed class UpdateInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("md5")] public string Md5 { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("size")] public string Size { get; set; } = "";
}

/// <summary>软件内热更新：查版本 → 下载 → 校验 → 替换文件 → 重启自己</summary>
public static class UpdateService
{
    /// <summary>当前客户端版本，例如 1.1.4</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>安装目录（exe 所在目录）</summary>
    public static string InstallDir =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
        ?? AppContext.BaseDirectory;

    public static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>查最新版本；拿不到返回 null</summary>
    public static async Task<UpdateInfo?> CheckAsync(string apiBaseUrl, CancellationToken ct = default)
    {
        try
        {
            return await CheckOnceAsync(apiBaseUrl, ct);
        }
        catch (Exception ex) when (NetClient.LooksLikeProxyFailure(ex))
        {
            // 系统代理连不上：改直连再试一次
            NetClient.SwitchToDirect();
            try
            {
                return await CheckOnceAsync(apiBaseUrl, ct);
            }
            catch
            {
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static async Task<UpdateInfo?> CheckOnceAsync(string apiBaseUrl, CancellationToken ct)
    {
        using var http = NetClient.Create(TimeSpan.FromSeconds(15));
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(apiBaseUrl + "?r=client_update", body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            return null;
        }
        return JsonSerializer.Deserialize<UpdateInfo>(
            doc.RootElement.GetProperty("data").GetRawText());
    }

    /// <summary>把 "v1.1.4" / "1.1.4" 解析成可比较的版本号</summary>
    public static Version Parse(string? s)
    {
        var t = (s ?? "").Trim().TrimStart('v', 'V');
        if (t.Length == 0)
        {
            return new Version(0, 0);
        }
        var parts = t.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var nums = new List<int>();
        foreach (var p in parts)
        {
            var digits = new string(p.TakeWhile(char.IsDigit).ToArray());
            nums.Add(int.TryParse(digits, out var n) ? n : 0);
            if (nums.Count == 4)
            {
                break;
            }
        }
        while (nums.Count < 2)
        {
            nums.Add(0);
        }
        return new Version(nums[0], nums[1], nums.Count > 2 ? nums[2] : 0);
    }

    public static bool IsNewer(string? remote, string? current)
        => Parse(remote) > Parse(current);

    public static string ResolveUrl(string apiBaseUrl, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
        {
            return abs.ToString();
        }
        return new Uri(new Uri(apiBaseUrl), url).ToString();
    }

    /// <summary>下载新版并解压，写好替换脚本；返回脚本路径（调用方随后退出程序即可）</summary>
    public static async Task<string> DownloadAndPrepareAsync(
        UpdateInfo info, string apiBaseUrl, IProgress<(int percent, string text)>? progress,
        CancellationToken ct = default)
    {
        var url = ResolveUrl(apiBaseUrl, info.Url);
        // 下载地址是固定文件名，加个版本参数避免被浏览器/代理缓存住旧包
        if (!string.IsNullOrWhiteSpace(info.Version))
        {
            url += (url.Contains('?') ? "&" : "?") + "v=" + Uri.EscapeDataString(info.Version);
        }
        var work = Path.Combine(Path.GetTempPath(), "aipai-update");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, true);
        }
        Directory.CreateDirectory(work);

        var zipPath = Path.Combine(work, "pkg.zip");
        progress?.Report((0, "正在下载安装包…"));

        using (var http = NetClient.Create(TimeSpan.FromMinutes(10)))
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            var last = -1;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0)
                {
                    var pct = (int)(read * 100 / total);
                    if (pct != last)
                    {
                        last = pct;
                        progress?.Report((pct, $"正在下载… {pct}%"));
                    }
                }
            }
        }

        progress?.Report((100, "正在校验文件…"));
        if (!string.IsNullOrWhiteSpace(info.Md5))
        {
            var actual = Md5Of(zipPath);
            if (!string.Equals(actual, info.Md5.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"安装包校验失败（MD5 不一致）。\n期望 {info.Md5}\n实际 {actual}");
            }
        }

        progress?.Report((100, "正在解压…"));
        var extractDir = Path.Combine(work, "new");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        // 压缩包里通常有一层文件夹，找到真正的程序目录。
        // 按「当前跑的这个 exe 的名字」去找：正式版叫 艾派互联.exe，旧版叫 艾派组网.exe，
        // 同一个更新接口下两个客户端都不会装错包。
        var myExe = Path.GetFileName(Environment.ProcessPath ?? "");
        if (myExe.Length == 0) { myExe = "艾派互联.exe"; }
        var sourceDir = extractDir;
        if (!File.Exists(Path.Combine(sourceDir, myExe)))
        {
            var sub = Directory.GetDirectories(extractDir)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, myExe)));
            if (sub == null)
            {
                throw new InvalidOperationException($"安装包里没有找到 {myExe}");
            }
            sourceDir = sub;
        }

        return PrepareScript(sourceDir);
    }

    /// <summary>
    /// 生成替换脚本：等本进程退出 → 覆盖安装目录 → 重新启动 → 清理。
    /// 用 PowerShell 而不是 .cmd：.cmd 只能按系统 ANSI 编码读，
    /// 安装路径里有中文（例如"艾派互联"）就会变成乱码导致找不到文件。
    /// </summary>
    public static string PrepareScript(string sourceDir)
    {
        var pid = Environment.ProcessId;
        var dst = InstallDir.TrimEnd('\\');
        var exe = ExePath;
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
        sb.AppendLine($"$waitPid={pid}");
        sb.AppendLine("while (Get-Process -Id $waitPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 700 }");
        sb.AppendLine("$ok=$false");
        sb.AppendLine("for ($i=0; $i -lt 30; $i++) {");
        sb.AppendLine($"  robocopy '{sourceDir}' '{dst}' /E /IS /IT /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null");
        sb.AppendLine("  if ($LASTEXITCODE -lt 8) { $ok=$true; break }");
        sb.AppendLine("  Start-Sleep -Seconds 1");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Milliseconds 500");
        sb.AppendLine($"Start-Process -FilePath '{exe}'");
        sb.AppendLine("Start-Sleep -Seconds 3");
        // 整个升级工作目录一起删掉（只删解压出来的那一层会留下 new / pkg.zip，白白占几十 MB）
        sb.AppendLine($"Remove-Item -LiteralPath '{Path.Combine(Path.GetTempPath(), "aipai-update")}' -Recurse -Force");
        sb.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force");
        sb.AppendLine($"if (-not $ok) {{ Set-Content -Path (Join-Path $env:TEMP 'aipai-update-fail.txt') -Value 'robocopy 未成功' }}");

        var script = Path.Combine(Path.GetTempPath(), "aipai-update", "apply-update.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        // 带 BOM 的 UTF-8：PowerShell 5.1 才会正确读中文路径
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(true));
        return script;
    }

    /// <summary>启动替换脚本（独立进程，不受本程序退出影响）</summary>
    public static void RunUpdater(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \""
                + scriptPath + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Path.GetTempPath(),
        };
        Process.Start(psi);
    }

    private static string Md5Of(string file)
    {
        using var md5 = MD5.Create();
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }
}
