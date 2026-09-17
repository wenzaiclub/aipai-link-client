using System.IO;
using AppiieNet.Client.Models;
using AppiieNet.Client.Services;

namespace AiPai.Compact.Services;

/// <summary>
/// 紧凑版客户端的全部运行状态：账号、组网列表、连接状态、速率。
/// 数据一律来自我们自己的后端（net.appiie.cn），引擎复用主客户端那套 EasyTierService。
/// </summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    public AppSettings Settings { get; }
    public ApiClient Api { get; }
    public EasyTierService Engine { get; } = new();

    public LoginData? Login { get; private set; }
    public List<NetworkSummary> Networks { get; private set; } = new();
    public List<NetworkMember> Members { get; private set; } = new();
    public NetworkSummary? Selected { get; set; }

    // idle / connecting / connected / failed
    public string ConnState { get; private set; } = "idle";
    public string LinkType { get; private set; } = "";
    public double RxKb { get; private set; }
    public double TxKb { get; private set; }
    public string StatusText { get; private set; } = "未连接";
    public bool Busy { get; private set; }

    public event Action? Changed;

    private CancellationTokenSource? _poll;
    private long _prevRx, _prevTx;
    private DateTime _prevAt = DateTime.UtcNow;
    private DateTime _lastHeartbeat = DateTime.MinValue;

    private AppState()
    {
        Settings = AppSettings.Load();
        if (string.IsNullOrWhiteSpace(Settings.DeviceUid))
        {
            Settings.DeviceUid = Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
            Settings.Save();
        }
        Api = new ApiClient(Settings.ApiBaseUrl);
    }

    public void Raise() => Changed?.Invoke();

    public bool LoggedIn => Login != null && !string.IsNullOrEmpty(Login.Token);
    public string Username => Login?.User.Username ?? Settings.Username;
    public string PlanName => Login?.User.Level?.Name
                             ?? (Login?.User.Plan is "pro" ? "PRO会员" : "免费版");
    public string DeviceCountText => Login == null
        ? ""
        : $"设备 {Login.Devices.Count}/{Login.MaxDevices}";
    public string DisplayIp => Selected?.DisplayIpv4 ?? "-";
    public string ServerLocation => Selected?.ServerLocation ?? "-";
    public string RoleText => Selected?.RoleText ?? "-";
    public string ShortDeviceId
    {
        get
        {
            var id = Settings.DeviceUid ?? "";
            return id.Length >= 6 ? id[^6..].ToUpperInvariant() : id.ToUpperInvariant();
        }
    }

    public string ConnStateText => ConnState switch
    {
        "connecting" => "连接中",
        "connected" => "已连接",
        "failed" => "连接失败",
        _ => "未连接",
    };

    // ---------- 账号 ----------

    public async Task<string?> LoginAsync(string username, string password, bool remember)
    {
        var data = await Api.LoginAsync(username, password, Settings.DeviceUid, Settings.DeviceName);
        Login = data;
        Settings.Token = data.Token;
        Settings.Username = data.User.Username;
        if (remember) { Settings.SaveCredentials(username, password); } else { Settings.ClearCredentials(); }
        Settings.Save();
        Networks = data.Networks;
        Selected = Networks.FirstOrDefault();
        AppLog.Client($"紧凑版登录成功：{data.User.Username}");
        StartAutoRefresh();
        Raise();
        return null;
    }

    public async Task<string?> RegisterAsync(string username, string password, string email, string invite)
    {
        await Api.RegisterAsync(username, password, email, invite);
        AppLog.Client($"紧凑版注册成功：{username}");
        return null;
    }

    public async Task<bool> TryResumeAsync()
    {
        if (string.IsNullOrEmpty(Settings.Token)) { return false; }
        try
        {
            var data = await Api.MeAsync(Settings.Token);
            // me 接口不回传 token，这里补回去，否则 LoggedIn 会判定为未登录，界面又退回登录页
            data.Token = Settings.Token;
            Login = data;
            Networks = data.Networks;
            Selected = Networks.FirstOrDefault();
            StartAutoRefresh();
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Client("自动登录失败：" + ex.Message);
            Settings.Token = "";
            Settings.Save();
            return false;
        }
    }

    public void Logout()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        Login = null;
        Networks = new List<NetworkSummary>();
        Members = new List<NetworkMember>();
        Selected = null;
        Settings.Token = "";
        Settings.Save();
        Raise();
    }

    // ---------- 组网 ----------

    public async Task RefreshNetworksAsync()
    {
        if (!LoggedIn) { return; }
        var data = await Api.ListNetworksAsync(Login!.Token);
        Networks = data.Networks;

        // 服务端已经删掉的组网：本地成员缓存、选中的组网、设置里记的 CurrentNetworkId 一起清掉。
        // 不清的话列表里会留着「幽灵组网」，点连接还会报「组网不存在或已关闭」。
        var live = new HashSet<int>(Networks.Select(n => n.Id));
        foreach (var gone in _members.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _members.Remove(gone);
            _loadingMembers.Remove(gone);
            AppLog.Client($"[清理] 组网 #{gone} 在服务端已不存在，已从本地列表移除");
        }
        if (Settings.CurrentNetworkId is int cur && cur > 0 && !live.Contains(cur))
        {
            Settings.CurrentNetworkId = null;
            Settings.Save();
        }
        if (Selected != null && !live.Contains(Selected.Id))
        {
            if (ConnState == "connected") { await DisconnectAsync(); }
            Selected = null;
        }
        Selected = Selected != null
            ? (Networks.FirstOrDefault(n => n.Id == Selected.Id) ?? Networks.FirstOrDefault())
            : Networks.FirstOrDefault();
        await RefreshMembersAsync();
        Raise();
    }

    /// <summary>
    /// 面板上那个刷新按钮：重新拉组网列表（顺带清掉已删的）、刷新当前组网成员，
    /// 连着的再顺手读一次引擎链路（直连/中继）和速率。
    /// </summary>
    public async Task RefreshStatusAsync()
    {
        if (!LoggedIn) { return; }
        try
        {
            await RefreshNetworksAsync();
            await RefreshAllMembersAsync();      // 每张卡片的成员在线状态一起更新
        }
        catch (Exception ex)
        {
            StatusText = "刷新失败：" + ex.Message;
            AppLog.Client("[刷新] 拉组网列表失败：" + ex.Message);
        }
        if (ConnState == "connected")
        {
            try
            {
                var peers = await Engine.GetPeersAsync();
                var remote = peers.Where(p => !p.IsLocal).ToList();
                LinkType = remote.Count > 0 ? (remote.Any(p => p.IsRelay) ? "中继" : "P2P 直连") : "";
            }
            catch { }
        }
        Raise();
    }

    // ---------- 后台每 60 秒自动刷一次组网列表 ----------

    private CancellationTokenSource? _autoRefresh;

    public void StartAutoRefresh()
    {
        _autoRefresh?.Cancel();
        _autoRefresh = new CancellationTokenSource();
        var ct = _autoRefresh.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { return; }
                if (ct.IsCancellationRequested || !LoggedIn) { continue; }
                try
                {
                    // 别的设备上把组网删了，这里 1 分钟内自动消失
                    await RefreshNetworksAsync();
                    // 成员在线状态（每个组网卡片各有一份缓存）也一起刷
                    await RefreshAllMembersAsync();
                }
                catch { }
            }
        });
    }

    public void StopAutoRefresh() => _autoRefresh?.Cancel();

    public async Task RefreshMembersAsync()
    {
        if (!LoggedIn || Selected == null) { Members = new List<NetworkMember>(); return; }
        try
        {
            var info = await Api.NetworkInfoAsync(Selected.Id, Login!.Token);
            Members = info.Members;
        }
        catch { Members = new List<NetworkMember>(); }
    }

    /// <summary>
    /// 把这台设备所在的每个组网的成员列表都刷一遍。
    /// 面板里每张组网卡片是各自缓存成员的，只刷「当前选中的那个」会让别的卡片一直显示旧状态，
    /// 表现就是「设备明明在线，状态不更新」。
    /// </summary>
    public async Task RefreshAllMembersAsync(int limit = 12)
    {
        if (!LoggedIn) { return; }
        var ids = Networks.Select(n => n.Id).Take(limit).ToList();
        foreach (var id in ids)
        {
            try
            {
                var info = await Api.NetworkInfoAsync(id, Login!.Token);
                _members[id] = info.Members;
                if (Selected != null && Selected.Id == id) { Members = info.Members; }
            }
            catch { /* 单个组网失败不影响其它 */ }
        }
        Raise();
    }

    // ---------- 各组成员（用于列表里展开显示设备） ----------

    private readonly Dictionary<int, List<NetworkMember>> _members = new();
    private readonly HashSet<int> _loadingMembers = new();

    public IReadOnlyList<NetworkMember> MembersOf(int networkId) =>
        _members.TryGetValue(networkId, out var list) ? list : Array.Empty<NetworkMember>();

    /// <summary>本账号下的设备（登录过这个账号的所有设备）</summary>
    public IReadOnlyList<DeviceInfo> MyDevices => Login?.Devices ?? new List<DeviceInfo>();

    /// <summary>
    /// 某台设备是否在线：优先看各组成员表里的实时状态，
    /// 组表里没有该设备时再退回 last_seen 的时间判断。返回 null 表示无法判断。
    /// </summary>
    public bool? DeviceOnline(DeviceInfo d)
    {
        foreach (var pair in _members)
        {
            var hit = pair.Value.FirstOrDefault(m => m.DeviceId == d.Id);
            if (hit != null) { return hit.Online; }
        }
        if (!string.IsNullOrWhiteSpace(d.LastSeen)
            && DateTime.TryParse(d.LastSeen, out var seen))
        {
            var diff = DateTime.Now - seen;
            if (diff < TimeSpan.Zero) { diff = diff.Negate(); }
            return diff.TotalMinutes <= 5;
        }
        return null;
    }

    /// <summary>这一组的成员还没取过、也没正在取 —— 界面据此决定要不要发起一次请求</summary>
    public bool NeedsMembers(int networkId) =>
        !_members.ContainsKey(networkId) && !_loadingMembers.Contains(networkId);

    public async Task LoadMembersAsync(int networkId)
    {
        if (!LoggedIn) { return; }
        _loadingMembers.Add(networkId);
        try
        {
            var info = await Api.NetworkInfoAsync(networkId, Login!.Token);
            _members[networkId] = info.Members;
        }
        catch { _members[networkId] = new List<NetworkMember>(); }
        finally { _loadingMembers.Remove(networkId); }
        Raise();
    }

    /// <summary>退出组网（成员退出；房主退出等同离开该组网）</summary>
    public async Task LeaveNetworkAsync(NetworkSummary n)
    {
        await Api.LeaveNetworkAsync(n.Id, Login!.Token);
        _members.Remove(n.Id);
        AppLog.Client($"退出组网：{n.Name}");
        if (Selected?.Id == n.Id)
        {
            await DisconnectAsync();
            Selected = null;
        }
        await RefreshNetworksAsync();
    }

    /// <summary>最近一次挑节点的结果，界面上顺带显示一句「节点 上海（32ms）」</summary>
    public string PickedNodeText { get; private set; } = "";

    /// <summary>
    /// 挑一个离本机最近的节点：并发测每个节点的 TCP 握手时间，取最快的那台。
    /// 一个都测不到（断网、全被墙）就退回服务端默认节点，不阻塞创建组网。
    /// </summary>
    public async Task<string> PickNodeIdAsync()
    {
        if (!LoggedIn) { return ""; }
        try
        {
            var data = await Api.NodesAsync(Login!.Token);
            var pick = await NodePicker.PickFastestAsync(data.Nodes);
            if (pick == null) { PickedNodeText = ""; return ""; }
            PickedNodeText = "节点已按延迟自动选择：" + pick.Text;
            AppLog.Client($"[选节点] {pick.Name} {pick.LatencyMs}ms（{pick.Id}）");
            return pick.Id;
        }
        catch (Exception ex)
        {
            AppLog.Client("[选节点] 失败，改用服务端默认：" + ex.Message);
            PickedNodeText = "";
            return "";
        }
    }

    public async Task<RoomConfig> CreateNetworkAsync(string name)
    {
        var nodeId = await PickNodeIdAsync();
        var cfg = await Api.CreateNetworkAsync(name, nodeId, Login!.Token);
        AppLog.Client($"创建组网：{cfg.Name}（适配码 {cfg.Code}）");
        await RefreshNetworksAsync();
        Selected = Networks.FirstOrDefault(n => n.Id == cfg.Id) ?? Selected;
        Raise();
        return cfg;
    }

    public async Task<RoomConfig> JoinNetworkAsync(string code)
    {
        var cfg = await Api.JoinNetworkAsync(code.Trim().ToUpperInvariant(), Login!.Token);
        AppLog.Client($"加入组网：{cfg.Name}（适配码 {cfg.Code}）");
        await RefreshNetworksAsync();
        Selected = Networks.FirstOrDefault(n => n.Id == cfg.Id) ?? Selected;
        Raise();
        return cfg;
    }

    public async Task ConnectAsync()
    {
        if (!LoggedIn) { return; }
        if (Selected == null) { StatusText = "请先选择组网"; Raise(); return; }

        try
        {
            Busy = true;
            ConnState = "connecting";
            StatusText = "正在连接…";
            Raise();

            // 锚点可能刚被服务端拉起来（第一次连接、或服务端重启过），
            // 这时接口会返回 ROOM_NOT_READY —— 等它起来，而不是直接报错。
            RoomConfig? cfg = null;
            var deadline = DateTime.UtcNow.AddMinutes(2);
            var waited = 0;
            var autoJoined = false;
            while (cfg == null)
            {
                try
                {
                    cfg = await Api.GetNetworkConfigAsync(Selected.Id, Login!.Token);
                }
                catch (ApiException ex) when (ex.Code == "ROOM_NOT_READY")
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        ConnState = "failed";
                        StatusText = "组网锚点等了 2 分钟还没起来，过会儿再试";
                        AppLog.Client("连接失败：锚点未就绪（等待超时）");
                        return;
                    }
                    waited += 5;
                    StatusText = $"组网锚点启动中…已等 {waited} 秒";
                    Raise();
                    await Task.Delay(5000);
                }
                catch (ApiException ex) when (!autoJoined && ex.Message.Contains("不在该组网"))
                {
                    // 这台设备还没有成员记录（换机器、重装、或者用另一个客户端登录）
                    // → 自动用适配码加入，然后再取配置
                    autoJoined = true;
                    StatusText = "本机还没加入该组网，正在用适配码加入…";
                    Raise();
                    await Api.JoinNetworkAsync(Selected.Code, Login!.Token);
                    await RefreshNetworksAsync();
                    AppLog.Client($"自动加入组网「{Selected.Name}」（适配码 {Selected.Code}）");
                }
            }

            EnsureEngineDir();
            Engine.Start(cfg, Settings.BroadcastRelay);

            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalSeconds < 30)
            {
                if (Engine.VirtualIpUp(cfg.Ipv4))
                {
                    ConnState = "connected";
                    StatusText = "已连接";
                    _prevRx = 0; _prevTx = 0; _prevAt = DateTime.UtcNow;
                    StartPolling();
                    AppLog.Client($"已连接「{cfg.Name}」，本机IP {cfg.Ipv4}");
                    return;
                }
                await Task.Delay(600);
            }

            Engine.Stop();
            ConnState = "failed";
            StatusText = "虚拟网卡未建立（引擎起来了，但没拿到组网 IP）";
            AppLog.Client("连接失败：虚拟网卡未建立");
        }
        catch (Exception ex)
        {
            ConnState = "failed";
            StatusText = ex.Message;
            AppLog.Client("连接失败：" + ex.Message);
        }
        finally
        {
            Busy = false;
            Raise();
        }
    }

    public Task DisconnectAsync()
    {
        try { _poll?.Cancel(); } catch { }
        _poll = null;
        try
        {
            if (Engine.IsRunning) { Engine.Stop(); AppLog.Client("已断开组网"); }
        }
        catch { }
        ConnState = "idle";
        LinkType = "";
        RxKb = 0; TxKb = 0;
        StatusText = "未连接";
        Raise();
        return Task.CompletedTask;
    }

    public Task ToggleAsync() => ConnState == "connected" ? DisconnectAsync() : ConnectAsync();

    private void StartPolling()
    {
        _poll?.Cancel();
        _poll = new CancellationTokenSource();
        var ct = _poll.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var peers = await Engine.GetPeersAsync();
                    var changed = false;
                    if (peers.Count > 0)
                    {
                        var remote = peers.Where(p => !p.IsLocal).ToList();
                        if (remote.Count > 0)
                        {
                            var lt = remote.Any(p => p.IsRelay) ? "中继" : "P2P 直连";
                            if (lt != LinkType) { LinkType = lt; changed = true; }
                        }
                        var rx = peers.Sum(p => p.RxBytes);
                        var tx = peers.Sum(p => p.TxBytes);
                        var now = DateTime.UtcNow;
                        var dt = (now - _prevAt).TotalSeconds;
                        if (dt > 0.5 && _prevRx > 0)
                        {
                            var nrx = Math.Max(0, (rx - _prevRx) / 1024.0 / dt);
                            var ntx = Math.Max(0, (tx - _prevTx) / 1024.0 / dt);
                            if (Math.Abs(nrx - RxKb) > 0.5 || Math.Abs(ntx - TxKb) > 0.5) { changed = true; }
                            RxKb = nrx; TxKb = ntx;
                        }
                        _prevRx = rx; _prevTx = tx; _prevAt = now;
                    }
                    if (!Engine.IsRunning)
                    {
                        if (ConnState != "idle")
                        {
                            ConnState = "idle";
                            StatusText = "未连接";
                            changed = true;
                        }
                    }

                    // 心跳：服务端按 last_seen（90 秒内）判断成员在线，
                    // 不上报的话别人看我们永远是"离线"，自己那行也不会变绿。
                    if (Engine.IsRunning && Selected != null
                        && (DateTime.UtcNow - _lastHeartbeat).TotalSeconds >= 30)
                    {
                        _lastHeartbeat = DateTime.UtcNow;
                        try { await Api.HeartbeatAsync(Login!.Token, Selected.Id); } catch { }
                    }

                    if (changed) { Raise(); }
                }
                catch { }
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }, ct);
    }

    /// <summary>引擎目录：优先程序目录下的 engine，其次本地开发用的几个位置。</summary>
    private static void EnsureEngineDir()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "engine");
        if (File.Exists(Path.Combine(local, "easytier-core.exe")))
        {
            Environment.SetEnvironmentVariable("APPIENET_ENGINE_DIR", local);
            return;
        }
        // 其次看环境变量指定的目录（自己编译时常用）
        var fromEnv = Environment.GetEnvironmentVariable("APPIENET_ENGINE_DIR");
        foreach (var cand in new[] { fromEnv })
        {
            if (string.IsNullOrWhiteSpace(cand)) { continue; }
            if (File.Exists(Path.Combine(cand, "easytier-core.exe")))
            {
                Environment.SetEnvironmentVariable("APPIENET_ENGINE_DIR", cand);
                return;
            }
        }
    }

    // ---------- 开机自启 ----------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "艾派组网紧凑版";

    public bool AutoStartEnabled
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(RunName) != null;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (k == null) { return; }
                if (value) { k.SetValue(RunName, "\"" + (Environment.ProcessPath ?? "") + "\""); }
                else { k.DeleteValue(RunName, false); }
            }
            catch { }
            Raise();
        }
    }
}
