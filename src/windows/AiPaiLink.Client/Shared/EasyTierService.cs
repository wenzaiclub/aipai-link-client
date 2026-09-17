using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AppiieNet.Client.Models;

namespace AppiieNet.Client.Services;

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

/// <summary>本机在 EasyTier 里的网络信息（用于网络诊断）</summary>
public sealed class LocalNetInfo
{
    public string Ipv4 { get; set; } = "-";
    public string NatType { get; set; } = "-";
    public string NatTypeRaw { get; set; } = "";
    public string PublicIp { get; set; } = "";
    public string Version { get; set; } = "";
    public bool IsSymmetric => NatTypeRaw.StartsWith("Sym", StringComparison.OrdinalIgnoreCase);
}

public sealed class EasyTierService
{
    private readonly object _gate = new();
    private Process? _process;

    public string? CoreExe { get; }
    public string? CliExe { get; }

    /// <summary>EasyTier 默认监听端口</summary>
    public const int BasePort = 11010;

    /// <summary>本次启动时服务端下发的虚拟 IP</summary>
    public string StartedIpv4 { get; private set; } = "";

    /// <summary>本次引擎实例的 RPC 端口（给 easytier-cli 用），未启动时为 0</summary>
    private int _rpcPort;

    /// <summary>
    /// 本机是不是真的挂上了这个虚拟 IP。
    /// 只看进程活着是不够的——引擎可能起来了但网卡没建出来，
    /// 那时候界面上不该显示“已连接”。
    /// </summary>
    public bool VirtualIpUp(string? ip = null)
    {
        var target = string.IsNullOrWhiteSpace(ip) ? StartedIpv4 : ip!;
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    continue;
                }
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.ToString() == target)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // 查询失败当作没挂上
        }
        return false;
    }

    public EasyTierService()
    {
        var engineDir = Environment.GetEnvironmentVariable("APPIENET_ENGINE_DIR");
        if (string.IsNullOrEmpty(engineDir))
        {
            engineDir = Path.Combine(AppContext.BaseDirectory, "engine");
        }

        foreach (var dir in new[] { engineDir })
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                var core = Path.Combine(dir, "easytier-core.exe");
                var cli = Path.Combine(dir, "easytier-cli.exe");
                if (File.Exists(core) && File.Exists(cli))
                {
                    CoreExe = core;
                    CliExe = cli;
                    break;
                }
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process != null && !_process.HasExited;
            }
        }
    }

    public void Start(RoomConfig cfg, bool broadcastRelay)
    {
        if (CoreExe == null)
        {
            throw new InvalidOperationException(
                "没找到 easytier-core.exe。把 EasyTier 引擎放进程序目录下的 engine 文件夹，"
                + "或者用环境变量 APPIENET_ENGINE_DIR 指定路径。");
        }

        lock (_gate)
        {
            StopLocked();
            KillStaleInstances();
            var args = new List<string>
            {
                "-i", cfg.Ipv4 ?? throw new InvalidOperationException("服务端未下发 IPv4"),
                "--network-name", cfg.EzNetworkName,
                "--network-secret", cfg.EzSecret,
                "-p", $"tcp://{cfg.ServerHost}:{cfg.AnchorPort ?? 11010}",
                "-p", $"udp://{cfg.ServerHost}:{cfg.AnchorPort ?? 11010}",
            };

            // EasyTier 默认监听 11010-11013。这几个端口被别的软件占了的话
            // 引擎会直接起不来（实测：WSL 的设备宿主就常驻 11010）。
            // 这里探测一下，被占就整组往后挪，并显式告诉引擎监听哪些端口。
            if (!PortFree(BasePort))
            {
                var b = FindFreeBasePort();
                if (b > 0)
                {
                    AppLog.Client($"默认监听端口 {BasePort} 被占用，改用 {b}-{b + 3}");
                    foreach (var url in ListenerUrls(b))
                    {
                        args.Add("-l");
                        args.Add(url);
                    }
                }
            }
            if (!string.IsNullOrEmpty(cfg.Ipv6))
            {
                args.Add("--ipv6");
                args.Add(cfg.Ipv6);
            }

            // 显式指定引擎的 RPC 端口给 easytier-cli 用。
            // EasyTier 默认是 127.0.0.1:15888，这个端口经常被别的软件占着
            // （实测这台机器上 WSL 的 wslrelay 就占着 11010 和 15888），
            // 一旦被占，easytier-cli 会连到别人的引擎上，面板显示的链路（直连/中继）就是错的。
            _rpcPort = FindFreePort(BasePort + 4);
            if (_rpcPort > 0)
            {
                args.Add("-r");
                args.Add($"127.0.0.1:{_rpcPort}");
            }
            if (broadcastRelay)
            {
                args.Add("--enable-udp-broadcast-relay");
                args.Add("true");
            }
            // 本月中继额度用完：不再回落中继，只走 P2P 直连。
            // 直连本来就不经过锚点，所以不限速也不计量。
            if (cfg.RelayBlocked)
            {
                args.Add("--p2p-only");
                args.Add("true");
            }
            var psi = new ProcessStartInfo
            {
                FileName = CoreExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 引擎输出以前是直接丢掉的，出问题只能靠猜；现在收进日志
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            StartedIpv4 = (cfg.Ipv4 ?? "").Split('/')[0].Trim();
            _process = Process.Start(psi);
            if (_process == null)
            {
                throw new InvalidOperationException("EasyTier 启动失败");
            }
            _process.OutputDataReceived += (_, e) => AppLog.Write("engine", e.Data);
            _process.ErrorDataReceived += (_, e) => AppLog.Write("engine", e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            AppLog.Client($"启动组网引擎：本机IP {cfg.Ipv4}，节点 "
                + $"{cfg.ServerHost}:{cfg.AnchorPort ?? 11010}"
                + (cfg.RelayBlocked ? "（本月中继额度已用完，只走直连）" : ""));
        }
    }

    /// <summary>这个端口现在能不能占用（TCP/UDP、IPv4/IPv6 都试一遍）</summary>
    private static bool PortFree(int port)
    {
        try
        {
            var t = new TcpListener(IPAddress.Any, port);
            t.Start();
            t.Stop();
        }
        catch
        {
            return false;
        }
        try
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
        catch
        {
            return false;
        }
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                var t6 = new TcpListener(IPAddress.IPv6Any, port);
                t6.Server.DualMode = false;
                t6.Start();
                t6.Stop();
            }
            catch
            {
                return false;
            }
            try
            {
                using var u6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>从 11110 往后找一组连续 4 个可用的端口；找不到返回 0</summary>
    private static int FindFreeBasePort()
    {
        for (var b = BasePort + 100; b < BasePort + 600; b += 10)
        {
            if (PortFree(b) && PortFree(b + 1) && PortFree(b + 2) && PortFree(b + 3))
            {
                return b;
            }
        }
        return 0;
    }

    /// <summary>找一个空闲的 TCP 端口（RPC 用，只要本机能连就行）</summary>
    private static int FindFreePort(int from)
    {
        for (var p = from; p < from + 200; p++)
        {
            if (PortFree(p))
            {
                return p;
            }
        }
        return 0;
    }

    /// <summary>监听地址列表，和 EasyTier 默认那套一致，只是端口整体挪一下</summary>
    private static IEnumerable<string> ListenerUrls(int b) => new[]
    {
        $"tcp://0.0.0.0:{b}", $"tcp://[::]:{b}",
        $"udp://0.0.0.0:{b}", $"udp://[::]:{b}",
        $"wg://0.0.0.0:{b + 1}", $"wg://[::]:{b + 1}",
        $"quic://0.0.0.0:{b + 2}", $"quic://[::]:{b + 2}",
        $"ws://0.0.0.0:{b + 1}/", $"ws://[::]:{b + 1}/",
        $"wss://0.0.0.0:{b + 2}/", $"wss://[::]:{b + 2}/",
        $"faketcp://0.0.0.0:{b + 3}",
    };

    /// <summary>清理同引擎目录下残留的 easytier-core 进程，避免端口/网卡冲突</summary>
    private void KillStaleInstances()
    {
        if (CoreExe == null)
        {
            return;
        }
        foreach (var proc in Process.GetProcessesByName("easytier-core"))
        {
            try
            {
                if (proc.Id == _process?.Id)
                {
                    continue;
                }
                var path = proc.MainModule?.FileName;
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                // 1) 同一个引擎目录下的残留：清掉，避免端口被占
                var sameDir = string.Equals(path, CoreExe, StringComparison.OrdinalIgnoreCase);

                // 2) 别的安装位置的残留引擎：客户端被强杀（升级、任务管理器结束）之后，
                //    引擎会留在后台继续占端口、甚至连着上一张网。装到 Program Files 之后，
                //    桌面那份便携版的引擎就属于这种，路径不同、名字一样，也要一起清掉。
                var otherInstall = path.IndexOf(@"\艾派互联\engine\", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf(@"\艾派组网\engine\", StringComparison.OrdinalIgnoreCase) >= 0;

                if (sameDir || otherInstall)
                {
                    proc.Kill(true);
                    proc.WaitForExit(3000);
                }
            }
            catch
            {
                // 无权限或进程已退出，忽略
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        if (_process == null)
        {
            return;
        }
        AppLog.Client("停止组网引擎");
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
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

    public async Task<string?> GetLocalIpAsync()
    {
        var json = await RunCliJsonAsync("-o", "json", "node").ConfigureAwait(false);
        if (json == null)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("ipv4_addr", out var v)
            ? v.GetString()
            : null;
    }

    public async Task<List<PeerView>> GetPeersAsync()
    {
        var json = await RunCliJsonAsync("-o", "json", "peer").ConfigureAwait(false);
        var result = new List<PeerView>();
        if (json == null)
        {
            return result;
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return result;
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
            result.Add(new PeerView
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

        return result;
    }

    /// <summary>本机虚拟IP、NAT类型、公网出口、引擎版本（网络诊断用）</summary>
    public async Task<LocalNetInfo?> GetLocalInfoAsync()
    {
        var json = await RunCliJsonAsync("-o", "json", "node").ConfigureAwait(false);
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
                info.NatTypeRaw = NatRawFromCode(code);
            }
            if (stun.TryGetProperty("public_ip", out var ips) && ips.ValueKind == JsonValueKind.Array)
            {
                var list = ips.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x));
                info.PublicIp = string.Join(" / ", list);
            }
        }
        return info;
    }

    /// <summary>EasyTier 的 NAT 类型枚举 → 中文</summary>
    public static string NatTextFromCode(int code) => code switch
    {
        1 => "公网（无NAT）",
        2 => "公网（无端口转换）",
        3 => "全锥形",
        4 => "受限锥形",
        5 => "端口受限锥形",
        6 => "对称型",
        7 => "对称型（防火墙）",
        8 => "对称型（递增）",
        9 => "对称型（递减）",
        _ => "未知",
    };

    public static string NatRawFromCode(int code) => code switch
    {
        1 => "OpenInternet",
        2 => "NoPAT",
        3 => "FullCone",
        4 => "Restricted",
        5 => "PortRestricted",
        6 => "Symmetric",
        7 => "SymUdpFirewall",
        8 => "SymmetricEasyInc",
        9 => "SymmetricEasyDec",
        _ => "Unknown",
    };

    public static string NatText(string raw) => raw switch
    {
        "OpenInternet" => "公网（无NAT）",
        "NoPAT" => "公网（无端口转换）",
        "FullCone" => "全锥形",
        "Restricted" => "受限锥形",
        "PortRestricted" => "端口受限锥形",
        "Symmetric" => "对称型",
        "SymUdpFirewall" => "对称型（防火墙）",
        "SymmetricEasyInc" => "对称型（递增）",
        "SymmetricEasyDec" => "对称型（递减）",
        "-" or "" => "-",
        _ => raw,
    };

    /// <summary>NAT 类型能不能打洞</summary>
    public static bool CanPunch(string raw)
        => raw is "OpenInternet" or "NoPAT" or "FullCone" or "Restricted" or "PortRestricted";

    private static string Get(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v))
        {
            var s = v.GetString();
            return string.IsNullOrEmpty(s) ? "-" : s;
        }
        return "-";
    }

    public static long ParseSize(string s)
    {
        s = s.Trim();
        if (s == "" || s == "-")
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

    private async Task<string?> RunCliJsonAsync(params string[] args)
    {
        if (CliExe == null)
        {
            return null;
        }
        var psi = new ProcessStartInfo
        {
            FileName = CliExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // 必须指定这次的引擎实例，否则会连到别的软件占用的默认 RPC 端口上
        if (_rpcPort > 0)
        {
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add($"127.0.0.1:{_rpcPort}");
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi);
        if (p == null)
        {
            return null;
        }
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(true);
            }
            catch
            {
                // ignore
            }
            return null;
        }
        var output = await outTask.ConfigureAwait(false);
        var err = await errTask.ConfigureAwait(false);
        if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }
        return output;
    }
}
