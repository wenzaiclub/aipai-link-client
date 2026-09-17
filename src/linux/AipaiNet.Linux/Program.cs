using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AppiieNet.Client.Models;
using AppiieNet.Client.Services;
using AppiieNet.Linux.Services;

namespace AppiieNet.Linux;

internal static class Program
{
    private static readonly LinuxSettings Cfg = LinuxSettings.Load();
    private static readonly ApiClient Api = new(Cfg.ApiBaseUrl);
    private static readonly ClientSession Session = new(Cfg);
    /// <summary>版本号直接取程序集版本，避免改了 csproj 忘了改这里</summary>
    private static readonly string Ver =
        typeof(Program).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 某些终端不支持设置编码
        }

        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        try
        {
            switch (cmd)
            {
                case "login":
                    return await LoginAsync(args);
                case "logout":
                    return Logout();
                case "me":
                    return await MeAsync();
                case "list":
                case "networks":
                    return await ListAsync();
                case "create":
                    return await CreateAsync(args);
                case "join":
                    return await JoinAsync(args);
                case "connect":
                    return await ConnectAsync(args);
                case "web":
                    return await WebAsync(args);
                case "share":
                    return await ShareCommandAsync(args);
                case "status":
                    return await StatusAsync();
                case "disconnect":
                    return Disconnect();
                case "devices":
                    return await DevicesAsync();
                case "version":
                case "-v":
                case "--version":
                    Console.WriteLine($"艾派互联 Linux 客户端 v{Ver}  ({Cfg.ApiBaseUrl})");
                    return 0;
                default:
                    Help();
                    return cmd == "help" || cmd == "-h" || cmd == "--help" ? 0 : 1;
            }
        }
        catch (ApiException ex)
        {
            Console.Error.WriteLine("✗ " + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("✗ " + ex.Message);
            return 1;
        }
    }

    private static void Help()
    {
        Console.WriteLine($"""
艾派互联 Linux 客户端 v{Ver}

用法:  aipai <命令> [参数]

  登录
    login <账号> <密码>      登录并保存登录状态
    logout                   退出登录（清除本地登录状态）
    me                       查看当前账号、会员等级与到期时间
    devices                  查看本账号登录过的设备

  组网
    list                     列出我加入的组网（含我的固定IP）
    create <名称>            创建组网，返回 8 位适配码
    join <适配码>            用适配码加入别人的组网
    connect [适配码|序号]     接入组网并保持运行（Ctrl+C 断开）
    web [--port 8787]        启动网页管理面板（浏览器里连/断组网）
    status                   查看当前连接状态与对端链路
    disconnect               断开并结束后台组网进程

  其它
    version                  显示版本
    help                     显示本帮助

提示:
  * connect 需要 root 权限（创建虚拟网卡），请用 sudo 运行
  * 首次使用请先 login，再 connect；引擎目录默认在程序旁边的 engine/
  * 同一台机器重连、重启后虚拟IP都不变，只有删除组网才会重新分配
""");
    }

    /* ---------------- 账号 ---------------- */

    private static async Task<int> LoginAsync(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("用法: aipai login <账号> <密码>");
            return 1;
        }
        var user = args[1];
        var data = await Api.LoginAsync(user, args[2], Cfg.DeviceUid, Cfg.DeviceName, "linux");
        Cfg.Username = user;
        Cfg.Token = data.Token;
        Cfg.Save();
        Console.WriteLine($"✓ 登录成功：{user}");
        PrintAccount(data);
        return 0;
    }

    private static int Logout()
    {
        Cfg.Token = "";
        Cfg.Save();
        Console.WriteLine("✓ 已退出登录");
        return 0;
    }

    private static async Task<int> MeAsync()
    {
        var me = await Api.MeAsync(await RequireTokenAsync());
        PrintAccount(me);
        return 0;
    }

    private static void PrintAccount(LoginData d)
    {
        var lv = d.User.Level;
        var expire = string.IsNullOrEmpty(lv?.ExpireAt) ? "永久" : lv!.ExpireAt;
        Console.WriteLine($"  账号：{d.User.Username}      等级：{lv?.Name ?? d.User.Plan}");
        Console.WriteLine($"  到期：{expire}");
        Console.WriteLine($"  设备：{d.Devices.Count}/{d.MaxDevices} 台");
    }

    private static async Task<int> DevicesAsync()
    {
        var me = await Api.MeAsync(await RequireTokenAsync());
        Console.WriteLine($"{"ID",-6}{"设备名",-24}{"平台",-10}最近在线");
        foreach (var dev in me.Devices)
        {
            Console.WriteLine($"{dev.Id,-6}{Cut(dev.DeviceName, 22),-24}{dev.Platform,-10}{dev.LastSeen ?? "-"}");
        }
        return 0;
    }

    /* ---------------- 组网 ---------------- */

    private static async Task<int> ListAsync()
    {
        var data = await Api.ListNetworksAsync(await RequireTokenAsync());
        if (data.Networks.Count == 0)
        {
            Console.WriteLine("还没有加入任何组网。可用 `aipai create 名称` 创建，或 `aipai join 适配码` 加入。");
            return 0;
        }
        Console.WriteLine($"{"#",-4}{"名称",-18}{"适配码",-12}{"角色",-8}{"本机固定IP",-18}节点");
        var i = 1;
        var hasNotJoined = false;
        foreach (var n in data.Networks)
        {
            var joined = !string.IsNullOrWhiteSpace(n.DeviceIpv4);
            var ip = joined ? n.DeviceIpv4! : "未加入";
            if (!joined)
            {
                hasNotJoined = true;
            }
            Console.WriteLine($"{i,-4}{Cut(n.Name, 16),-18}{n.Code,-12}{n.RoleText,-8}{ip,-18}{n.ServerLocation}");
            i++;
        }
        if (hasNotJoined)
        {
            Console.WriteLine("提示：显示“未加入”表示这台机器还没进该组网，执行 `sudo aipai connect 适配码` 会自动加入并分配固定IP。");
        }
        return 0;
    }

    private static async Task<int> CreateAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: aipai create <组网名称>");
            return 1;
        }
        // 节点按延迟自动挑：并发测每台的 TCP 握手时间，取最快的那台
        var myToken = await RequireTokenAsync();
        string nodeId = "";
        try
        {
            var nodes = await Api.NodesAsync(myToken);
            var pick = await NodePicker.PickFastestAsync(nodes.Nodes);
            if (pick != null)
            {
                nodeId = pick.Id;
                Console.WriteLine($"   节点已按延迟自动选择：{pick.Name} {pick.LatencyMs}ms");
            }
        }
        catch { }
        var cfg = await Api.CreateNetworkAsync(args[1], nodeId, myToken);
        Cfg.LastNetworkId = cfg.Id;
        Cfg.Save();
        Console.WriteLine($"✓ 已创建组网「{cfg.Name}」");
        Console.WriteLine($"  适配码：{cfg.Code}   （把这个码发给朋友，他们输入即可加入）");
        Console.WriteLine($"  你的固定IP：{cfg.Ipv4}");
        Console.WriteLine($"  接入组网：sudo aipai connect {cfg.Code}");
        return 0;
    }

    private static async Task<int> JoinAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: aipai join <适配码>");
            return 1;
        }
        var cfg = await Api.JoinNetworkAsync(args[1].Trim().ToUpperInvariant(), (await RequireTokenAsync()));
        Cfg.LastNetworkId = cfg.Id;
        Cfg.Save();
        Console.WriteLine($"✓ 已加入组网「{cfg.Name}」");
        Console.WriteLine($"  你的固定IP：{cfg.Ipv4}   （重连不会变）");
        Console.WriteLine($"  接入组网：sudo aipai connect {cfg.Code}");
        return 0;
    }

    private static async Task<int> ConnectAsync(string[] args)
    {
        var token = await RequireTokenAsync();
        var data = await Api.ListNetworksAsync(token);
        // 参数优先，其次环境变量 AIPAI_NETWORK（容器/服务用），都没有就用上次连接的组网
        var arg = args.Length >= 2
            ? args[1].Trim()
            : (Environment.GetEnvironmentVariable("AIPAI_NETWORK") ?? "").Trim();
        NetworkSummary? net = null;
        if (arg != "")
        {
            if (int.TryParse(arg, out var idx) && idx >= 1 && idx <= data.Networks.Count)
            {
                net = data.Networks[idx - 1];
            }
            else
            {
                // 先按适配码 / 组网名称匹配已有的组网
                net = data.Networks.FirstOrDefault(n =>
                        string.Equals(n.Code, arg, StringComparison.OrdinalIgnoreCase))
                    ?? data.Networks.FirstOrDefault(n =>
                        string.Equals(n.Name, arg, StringComparison.OrdinalIgnoreCase))
                    ?? data.Networks.FirstOrDefault(n =>
                        n.Name.StartsWith(arg, StringComparison.OrdinalIgnoreCase));
                if (net == null)
                {
                    // 都没匹配上，就当成新的适配码去加入
                    if (arg.Length != 8)
                    {
                        Console.Error.WriteLine($"✗ 没有找到叫「{arg}」的组网；如果要加入新组网，请输入 8 位适配码。");
                        return 1;
                    }
                    var joined = await Api.JoinNetworkAsync(arg.ToUpperInvariant(), token);
                    Cfg.LastNetworkId = joined.Id;
                    Cfg.Save();
                    net = data.Networks.FirstOrDefault(n => n.Id == joined.Id)
                        ?? new NetworkSummary { Id = joined.Id, Name = joined.Name, Code = joined.Code };
                }
            }
        }
        else
        {
            net = data.Networks.FirstOrDefault(n => n.Id == Cfg.LastNetworkId)
                ?? data.Networks.FirstOrDefault();
        }
        if (net == null)
        {
            Console.Error.WriteLine("没有可连接的组网：先 `aipai create 名称` 或 `aipai join 适配码`。");
            return 1;
        }
        Cfg.LastNetworkId = net.Id;
        Cfg.Save();

        if (!Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("✗ 接入组网需要 root 权限（要创建虚拟网卡），请用: sudo aipai connect");
            return 1;
        }

        var engine = new LinuxEngine(Cfg.EngineDir);
        if (engine.CoreExe == null)
        {
            Console.Error.WriteLine("✗ 没找到 easytier-core。把 engine 目录放到程序旁边，或用 --engine 指定。");
            return 1;
        }

        Console.WriteLine($"正在接入组网「{net.Name}」…");
        RoomConfig cfg;
        try
        {
            cfg = await WaitAnchorAsync(net.Id);
        }
        catch (ApiException ex) when (ex.Code == "NOT_FOUND")
        {
            // 这台机器还没加入该组网：直接用适配码加入
            Console.WriteLine($"  本机还没加入「{net.Name}」，正在用适配码 {net.Code} 加入…");
            await Api.JoinNetworkAsync(net.Code, token);
            cfg = await WaitAnchorAsync(net.Id);
        }
        engine.Start(cfg);
        await Task.Delay(2500);
        if (!engine.IsRunning)
        {
            Console.Error.WriteLine("✗ 组网进程启动失败（可能是旧进程占用或权限问题）。");
            return 1;
        }

        Console.WriteLine($"✓ 已接入组网「{cfg.Name}」");
        Console.WriteLine($"  本机固定IP：{cfg.Ipv4}     （只要不删除组网就不会变）");
        Console.WriteLine("  按 Ctrl+C 断开。");

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        // 被 kill / systemctl stop 时也要关掉引擎，别留残留进程
        PosixSignalRegistration? sigterm = null;
        try
        {
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ =>
            {
                engine.Stop();
                Environment.Exit(0);
            });
        }
        catch
        {
            // 忽略
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => engine.Stop();

        var tick = 0;
        while (!stop.Task.IsCompleted)
        {
            await Task.WhenAny(stop.Task, Task.Delay(3000));
            if (stop.Task.IsCompleted)
            {
                break;
            }
            tick++;
            try
            {
                if (tick % 5 == 0)
                {
                    await Api.HeartbeatAsync(token, net.Id);
                }
                var peers = await engine.PeersAsync();
                var others = peers.Where(p => p.Type != "本机").ToList();
                var p2p = others.Count(p => p.Type == "P2P直连");
                var relay = others.Count(p => p.Type == "中继");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 在线对端 {others.Count} 个"
                    + $"（P2P {p2p} · 中继 {relay}）  本机IP {cfg.Ipv4}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 状态刷新失败：{ex.Message}");
            }
        }

        engine.Stop();
        sigterm?.Dispose();
        Console.WriteLine("✓ 已断开组网");
        return 0;
    }

    private static async Task<RoomConfig> WaitAnchorAsync(int networkId, int maxSeconds = 150)
    {
        var waited = 0;
        while (true)
        {
            try
            {
                var cfg = await Api.GetNetworkConfigAsync(networkId, (await RequireTokenAsync()));
                if (cfg.AnchorStatus == "active")
                {
                    return cfg;
                }
            }
            catch (ApiException ex) when (ex.Code == "ROOM_NOT_READY")
            {
                // 锚点还在启动
            }
            if (waited >= maxSeconds)
            {
                throw new InvalidOperationException("组网锚点启动超时，请稍后重试。");
            }
            await Task.Delay(4000);
            waited += 4;
            Console.WriteLine($"  组网锚点启动中，已等待 {waited} 秒…");
        }
    }

    private static async Task<int> StatusAsync()
    {
        var engine = new LinuxEngine(Cfg.EngineDir);
        var ip = await engine.LocalIpAsync();
        if (ip == null)
        {
            Console.WriteLine("当前未接入组网（没有检测到运行中的 easytier-core）。");
            Console.WriteLine("接入命令：sudo aipai connect");
            return 0;
        }
        Console.WriteLine($"本机虚拟IP：{ip.Split('/')[0]}");
        var peers = await engine.PeersAsync();
        if (peers.Count == 0)
        {
            Console.WriteLine("暂无对端信息。");
            return 0;
        }
        Console.WriteLine($"{"类型",-10}{"虚拟IP",-16}{"主机名",-22}{"延迟",-10}{"丢包",-8}通道");
        foreach (var p in peers)
        {
            Console.WriteLine($"{p.Type,-10}{p.Ip,-16}{Cut(p.Host, 20),-22}{p.Latency,-10}{p.Loss,-8}{p.Tunnel}");
        }
        return 0;
    }

    private static int Disconnect()
    {
        var killed = 0;
        foreach (var p in Process.GetProcessesByName("easytier-core"))
        {
            try
            {
                p.Kill(true);
                p.WaitForExit(3000);
                killed++;
            }
            catch
            {
                // 可能是别的软件在用，忽略
            }
            finally
            {
                p.Dispose();
            }
        }
        Console.WriteLine(killed > 0 ? $"✓ 已断开组网（结束了 {killed} 个进程）" : "当前没有运行中的组网进程。");
        return 0;
    }

    /* ---------------- 小工具 ---------------- */

    /// <summary>登录令牌（含环境变量自动登录、令牌失效自愈），逻辑在 ClientSession 里</summary>
    private static async Task<string> RequireTokenAsync() => await Session.EnsureTokenAsync();

    /// <summary>网页管理面板：浏览器里看状态、连/断组网</summary>
    /// <summary>
    /// 文件 / 打印机共享：
    ///   aipai share                     看本机共享、队友共享、已挂载
    ///   aipai share add &lt;目录&gt; [名字] [--ro]
    ///   aipai share remove &lt;名字&gt;
    ///   aipai share mount &lt;\\ip\名字&gt; [挂载点]
    ///   aipai share umount &lt;挂载点&gt;
    ///   aipai share printer             开启打印机共享（CUPS）
    /// </summary>
    private static async Task<int> ShareCommandAsync(string[] args)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("✗ 共享功能需要 root 权限，请用: sudo aipai share");
            return 1;
        }
        var sub = args.Length >= 2 ? args[1].ToLowerInvariant() : "list";
        var myIp = "";
        var info = await Session.Engine.GetLocalInfoAsync();
        if (info != null)
        {
            myIp = info.Ipv4;
        }

        switch (sub)
        {
            case "list":
            case "ls":
            {
                if (await LinuxShare.HasSambaAsync())
                {
                    var mine = await LinuxShare.ListAsync(myIp);
                    Console.WriteLine("== 本机共享（给队友用）==");
                    if (mine.Count == 0)
                    {
                        Console.WriteLine("  （还没有）共享一个目录：sudo aipai share add /要共享的目录");
                    }
                    foreach (var s in mine)
                    {
                        Console.WriteLine($"  {(s.ReadOnly ? "[只读]" : "[读写]")} {s.Name,-16} {s.Path}");
                        Console.WriteLine($"          访问地址：{s.Unc}");
                    }
                }
                else
                {
                    Console.WriteLine("== 本机共享 ==");
                    Console.WriteLine("  还没装 Samba（文件共享组件）。第一次共享时会自动安装。");
                }

                Console.WriteLine();
                Console.WriteLine("== 组网内队友的共享 ==");
                // 排除本机（连接刚建立时本机条目可能还没标成 Local，所以再按 IP 兜一次底）
                var peers = (await Session.Engine.PeersAsync())
                    .Where(p => !p.IsLocal && p.Ip != myIp).ToList();
                if (peers.Count == 0)
                {
                    Console.WriteLine("  （没有其他在线成员，或还没接入组网）");
                }
                else
                {
                    var found = await LinuxShare.ScanAsync(peers.Select(p => p.Ip));
                    if (found.Count == 0)
                    {
                        Console.WriteLine($"  扫了 {peers.Count} 台设备，没发现共享（对方可能没开共享，或不是同一账号权限）");
                    }
                    foreach (var s in found)
                    {
                        Console.WriteLine($"  {s.KindText}  {s.Unc}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine("== 已挂载的队友共享 ==");
                var mounts = await LinuxShare.ListMountsAsync();
                if (mounts.Count == 0)
                {
                    Console.WriteLine("  （还没有）挂载一个：sudo aipai share mount '\\\\10.x.x.x\\共享名' /mnt/xx");
                }
                foreach (var m in mounts)
                {
                    Console.WriteLine($"  {m.Unc}  →  {m.Point}");
                }
                return 0;
            }

            case "add":
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("用法: sudo aipai share add <目录> [共享名] [--ro]");
                    return 1;
                }
                var path = Path.GetFullPath(args[2]);
                var readOnly = args.Any(a => a == "--ro" || a == "-r");
                var name = args.Length >= 4 && !args[3].StartsWith('-') ? args[3] : "";
                Console.WriteLine("==> 检查共享组件…");
                Console.WriteLine("    " + await LinuxShare.EnsureSambaAsync(t => Console.WriteLine("    " + t)));
                await LinuxShare.AddAsync(path, name, readOnly, myIp);
                var finalName = name.Length > 0 ? name : Path.GetFileName(path.TrimEnd('/'));
                Console.WriteLine($"✓ 已共享：{path}");
                Console.WriteLine($"  访问地址：\\\\{(myIp.Length > 0 ? myIp : "本机虚拟IP")}\\{finalName}");
                Console.WriteLine("  队友在资源管理器里输入上面的地址即可打开（Windows 端也可以“映射到本地”）。");
                return 0;
            }

            case "remove":
            case "rm":
            case "del":
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("用法: sudo aipai share remove <共享名>");
                    return 1;
                }
                await LinuxShare.RemoveAsync(args[2]);
                Console.WriteLine("✓ 已取消共享：" + args[2]);
                return 0;
            }

            case "mount":
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine(@"用法: sudo aipai share mount \\10.x.x.x\共享名 [挂载点]");
                    return 1;
                }
                var unc = args[2].Replace('/', '\\').TrimEnd('\\');
                var point = args.Length >= 4
                    ? args[3]
                    : "/mnt/aipai-" + unc.Split('\\').Last();
                var user = Environment.GetEnvironmentVariable("AIPAI_SHARE_USER") ?? "";
                var pass = Environment.GetEnvironmentVariable("AIPAI_SHARE_PASSWORD") ?? "";
                await LinuxShare.EnsureSambaAsync();
                Console.WriteLine($"==> 挂载 {unc} → {point}");
                await LinuxShare.MountAsync(unc, point, user, pass);
                Console.WriteLine($"✓ 已挂载，进入 {point} 就能看到对方的文件");
                Console.WriteLine($"  取消挂载：sudo aipai share umount {point}");
                return 0;
            }

            case "umount":
            case "unmount":
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("用法: sudo aipai share umount <挂载点>");
                    return 1;
                }
                await LinuxShare.UnmountAsync(args[2]);
                Console.WriteLine("✓ 已取消挂载：" + args[2]);
                return 0;
            }

            case "printer":
            case "printers":
            {
                var st = await LinuxShare.PrinterStatusAsync();
                Console.WriteLine(st.Installed
                    ? (st.Printers.Length > 0 ? "本机打印机：\n" + st.Printers : "本机还没有添加打印机")
                    : "还没装 CUPS（打印机服务），正在安装…");
                Console.WriteLine("==> " + await LinuxShare.EnablePrinterSharingAsync(
                    t => Console.WriteLine("    " + t)));
                var ip = myIp.Length > 0 ? myIp : "本机虚拟IP";
                Console.WriteLine($"✓ 打印机共享已开启，队友可以添加：");
                Console.WriteLine($"  Windows：添加打印机 → 按名称 → \\\\{ip}\\打印机名");
                Console.WriteLine($"  Linux：  sudo aipai share mount '\\\\{ip}\\打印机名'  （或不挂载，直接 IPP 连接）");
                return 0;
            }

            default:
                Console.WriteLine("""
用法：
  sudo aipai share                              看本机共享、队友共享、已挂载
  sudo aipai share add <目录> [共享名] [--ro]    共享一个目录（第一次会自动装 Samba）
  sudo aipai share remove <共享名>               取消共享
  sudo aipai share mount <\\虚拟IP\共享名> [挂载点]  挂载队友的共享到本机目录
  sudo aipai share umount <挂载点>               取消挂载
  sudo aipai share printer                      开启打印机共享（CUPS）

说明：组网内全端口互通，共享地址直接用组网虚拟 IP，不需要路由器端口映射。
""");
                return 0;
        }
    }

    /// <summary>网页管理面板：浏览器里看状态、连/断组网</summary>
    private static async Task<int> WebAsync(string[] args)
    {
        var host = "0.0.0.0";
        var port = 8787;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                port = p;
            }
            else if (args[i] == "--host" && i + 1 < args.Length)
            {
                host = args[i + 1];
            }
            else if (args[i].StartsWith("--port=", StringComparison.Ordinal)
                     && int.TryParse(args[i][7..], out var p2))
            {
                port = p2;
            }
            else if (args[i].StartsWith("--host=", StringComparison.Ordinal))
            {
                host = args[i][7..];
            }
        }
        if (!Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("✗ 网页面板需要 root 权限（要创建虚拟网卡），请用: sudo aipai web");
            return 1;
        }
        Console.WriteLine($"艾派互联 Linux 客户端 v{Ver}");
        await new WebPanel(Session).RunAsync(host, port);
        return 0;
    }

    private static string Cut(string s, int max)
        => string.IsNullOrEmpty(s) ? "-" : (s.Length <= max ? s : s[..(max - 1)] + "…");
}
