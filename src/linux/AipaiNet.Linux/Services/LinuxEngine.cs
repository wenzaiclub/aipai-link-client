using System.Diagnostics;
using System.Text.Json;
using AppiieNet.Client.Models;

namespace AppiieNet.Linux.Services;

public sealed class PeerView
{
    public string Type { get; init; } = "未知";
    public string Ip { get; init; } = "";
    public string Host { get; init; } = "";
    public string Latency { get; init; } = "-";
    public string Loss { get; init; } = "-";
    public string Tunnel { get; init; } = "-";
    public string NatType { get; init; } = "-";
    public string NatTypeRaw { get; init; } = "";
    public bool IsLocal { get; init; }
    public bool IsRelay => Type.StartsWith("中继", StringComparison.Ordinal);
    public string LinkText => IsLocal ? "本机" : (IsRelay ? "中继" : (Type == "P2P直连" ? "直连" : Type));
    public long RxBytes { get; init; }
    public long TxBytes { get; init; }
}

/// <summary>本机在 EasyTier 里的网络信息（网页面板用）</summary>
public sealed class LocalNetInfo
{
    public string Ipv4 { get; set; } = "-";
    public string NatType { get; set; } = "-";
    public string NatTypeRaw { get; set; } = "";
    public string PublicIp { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>调用 Linux 版 easytier-core / easytier-cli</summary>
public sealed class LinuxEngine
{
    private Process? _process;

    public string? CoreExe { get; private set; }
    public string? CliExe { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    public LinuxEngine(string engineDirFromConfig)
    {
        foreach (var dir in CandidateDirs(engineDirFromConfig))
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                continue;
            }
            var core = Path.Combine(dir, "easytier-core");
            var cli = Path.Combine(dir, "easytier-cli");
            if (File.Exists(core))
            {
                CoreExe = core;
                CliExe = File.Exists(cli) ? cli : null;
                return;
            }
        }
        // 最后再试 PATH
        var inPath = FindInPath("easytier-core");
        if (inPath != null)
        {
            CoreExe = inPath;
            CliExe = FindInPath("easytier-cli");
        }
    }

    private static IEnumerable<string> CandidateDirs(string fromConfig)
    {
        yield return fromConfig;
        yield return Environment.GetEnvironmentVariable("APPIENET_ENGINE_DIR") ?? "";
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        yield return Path.Combine(exeDir, "engine");
        yield return Path.Combine(AppContext.BaseDirectory, "engine");
        yield return "/opt/aipai/engine";
        yield return "/usr/local/lib/aipai/engine";
    }

    private static string? FindInPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p))
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>启动组网（参数与 Windows 客户端完全一致）</summary>
    public void Start(RoomConfig cfg)
    {
        if (CoreExe == null)
        {
            throw new InvalidOperationException(
                "没有找到 easytier-core。请把 engine 目录放到程序旁边，"
                + "或用 --engine 指定目录 / 设置环境变量 APPIENET_ENGINE_DIR。");
        }
        Stop();

        var args = new List<string>
        {
            "-i", cfg.Ipv4 ?? throw new InvalidOperationException("服务端未下发 IPv4"),
            "--network-name", cfg.EzNetworkName,
            "--network-secret", cfg.EzSecret,
            "-p", $"tcp://{cfg.ServerHost}:{cfg.AnchorPort ?? 11010}",
            "-p", $"udp://{cfg.ServerHost}:{cfg.AnchorPort ?? 11010}",
        };
        if (!string.IsNullOrEmpty(cfg.Ipv6))
        {
            args.Add("--ipv6");
            args.Add(cfg.Ipv6);
        }
        // 本月中继额度用完：只走 P2P 直连，不再回落中继
        if (cfg.RelayBlocked)
        {
            args.Add("--p2p-only");
            args.Add("true");
        }

        var psi = new ProcessStartInfo
        {
            FileName = CoreExe,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(CoreExe) ?? "/tmp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("easytier-core 启动失败");

        // 引擎日志写文件，不干扰命令行输出
        var logPath = LogPath();
        _process.OutputDataReceived += (_, e) => AppendLog(logPath, e.Data);
        _process.ErrorDataReceived += (_, e) => AppendLog(logPath, e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>引擎日志路径</summary>
    public static string LogPath()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        var dir = Path.Combine(string.IsNullOrEmpty(home) ? "/tmp" : home, ".config", "aipai");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "engine.log");
    }

    private static void AppendLog(string path, string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
        try
        {
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + line + "\n");
        }
        catch
        {
            // 日志写失败不影响运行
        }
    }

    public void Stop()
    {
        if (_process == null)
        {
            return;
        }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
            // 进程可能已退出
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    /// <summary>本机虚拟 IP（通过 easytier-cli 查询，跨进程可用）</summary>
    public async Task<string?> LocalIpAsync()
    {
        var json = await RunCliAsync("-o", "json", "node");
        if (json == null)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("ipv4_addr", out var v) ? v.GetString() : null;
    }

    public async Task<string?> LocalHostnameAsync()
    {
        var json = await RunCliAsync("-o", "json", "node");
        if (json == null)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("hostname", out var v) ? v.GetString() : null;
    }

    public async Task<List<PeerView>> PeersAsync()
    {
        var list = new List<PeerView>();
        var json = await RunCliAsync("-o", "json", "peer");
        if (json == null)
        {
            return list;
        }
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var cost = Get(el, "cost");
            var type = cost switch
            {
                "Local" => "本机",
                "p2p" => "P2P直连",
                _ when cost.Contains("relay", StringComparison.OrdinalIgnoreCase) => "中继",
                _ => cost,
            };
            list.Add(new PeerView
            {
                Type = type,
                Ip = Get(el, "ipv4"),
                Host = Get(el, "hostname"),
                Latency = Get(el, "lat_ms"),
                Loss = Get(el, "loss_rate"),
                Tunnel = Get(el, "tunnel_proto"),
                NatType = NatText(Get(el, "nat_type")),
                NatTypeRaw = Get(el, "nat_type"),
                IsLocal = cost == "Local",
                RxBytes = ParseSize(Get(el, "rx_bytes")),
                TxBytes = ParseSize(Get(el, "tx_bytes")),
            });
        }
        return list;
    }

    /// <summary>本机虚拟IP、NAT类型、公网出口、引擎版本</summary>
    public async Task<LocalNetInfo?> GetLocalInfoAsync()
    {
        var json = await RunCliAsync("-o", "json", "node");
        if (json == null)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = new LocalNetInfo
        {
            Ipv4 = root.TryGetProperty("ipv4_addr", out var ip)
                ? (ip.GetString() ?? "-").Split('/')[0] : "-",
            Version = root.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "",
        };
        if (root.TryGetProperty("stun_info", out var stun) && stun.ValueKind == JsonValueKind.Object)
        {
            if (stun.TryGetProperty("udp_nat_type", out var nt) && nt.TryGetInt32(out var code))
            {
                info.NatType = NatTextFromCode(code);
                info.NatTypeRaw = code switch
                {
                    1 => "OpenInternet", 2 => "NoPAT", 3 => "FullCone", 4 => "Restricted",
                    5 => "PortRestricted", 6 => "Symmetric", 7 => "SymUdpFirewall",
                    8 => "SymmetricEasyInc", 9 => "SymmetricEasyDec", _ => "Unknown",
                };
            }
            if (stun.TryGetProperty("public_ip", out var ips) && ips.ValueKind == JsonValueKind.Array)
            {
                info.PublicIp = string.Join(" / ",
                    ips.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)));
            }
        }
        return info;
    }

    public static string NatTextFromCode(int code) => code switch
    {
        1 => "公网（无NAT）", 2 => "公网（无端口转换）", 3 => "全锥形", 4 => "受限锥形",
        5 => "端口受限锥形", 6 => "对称型", 7 => "对称型（防火墙）",
        8 => "对称型（递增）", 9 => "对称型（递减）", _ => "未知",
    };

    public static string NatText(string raw) => raw switch
    {
        "OpenInternet" => "公网（无NAT）", "NoPAT" => "公网（无端口转换）", "FullCone" => "全锥形",
        "Restricted" => "受限锥形", "PortRestricted" => "端口受限锥形", "Symmetric" => "对称型",
        "SymUdpFirewall" => "对称型（防火墙）", "SymmetricEasyInc" => "对称型（递增）",
        "SymmetricEasyDec" => "对称型（递减）", "" or "-" => "-", _ => raw,
    };

    private static string Get(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v))
        {
            var s = v.GetString();
            return string.IsNullOrEmpty(s) ? "-" : s;
        }
        return "-";
    }

    private static long ParseSize(string s)
    {
        s = s.Trim();
        if (s is "" or "-")
        {
            return 0;
        }
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^([0-9.]+)\s*([kKmMgGtT]?)");
        if (!m.Success)
        {
            return 0;
        }
        var v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var mul = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => 1024d,
            "M" => 1024d * 1024,
            "G" => 1024d * 1024 * 1024,
            "T" => 1024d * 1024 * 1024 * 1024,
            _ => 1d,
        };
        return (long)(v * mul);
    }

    private async Task<string?> RunCliAsync(params string[] args)
    {
        if (CliExe == null)
        {
            return null;
        }
        var psi = new ProcessStartInfo
        {
            FileName = CliExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        try
        {
            using var p = Process.Start(psi);
            if (p == null)
            {
                return null;
            }
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await p.WaitForExitAsync(cts.Token);
            var output = await outTask;
            await errTask;
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
