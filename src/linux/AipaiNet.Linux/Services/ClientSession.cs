using AppiieNet.Client.Models;
using AppiieNet.Client.Services;

namespace AppiieNet.Linux.Services;

/// <summary>登录、选组网、接入/断开——命令行和网页面板共用这一套逻辑</summary>
public sealed class ClientSession
{
    public LinuxSettings Cfg { get; }
    public ApiClient Api { get; }
    public LinuxEngine Engine { get; }
    public string Token { get; set; } = "";

    private bool _tokenReady;

    public ClientSession(LinuxSettings cfg)
    {
        Cfg = cfg;
        Api = new ApiClient(cfg.ApiBaseUrl);
        Engine = new LinuxEngine(cfg.EngineDir);
        Token = cfg.Token;
    }

    public static (string User, string Pass) EnvCreds()
        => (Environment.GetEnvironmentVariable("AIPAI_USER") ?? "",
            Environment.GetEnvironmentVariable("AIPAI_PASSWORD") ?? "");

    /// <summary>
    /// 取可用令牌。
    /// 设了 AIPAI_USER / AIPAI_PASSWORD 时（systemd / Docker / NAS 场景），
    /// 本地令牌失效会自动重新登录，保证服务能自愈。
    /// </summary>
    public async Task<string> EnsureTokenAsync()
    {
        if (_tokenReady && !string.IsNullOrEmpty(Token))
        {
            return Token;
        }
        var (user, pass) = EnvCreds();
        var hasEnv = user != "" && pass != "";

        if (!string.IsNullOrEmpty(Token))
        {
            if (!hasEnv)
            {
                _tokenReady = true;
                return Token;
            }
            try
            {
                await Api.MeAsync(Token);
                _tokenReady = true;
                return Token;
            }
            catch (ApiException)
            {
                Token = "";
            }
        }

        if (hasEnv)
        {
            var data = await Api.LoginAsync(user, pass, Cfg.DeviceUid, Cfg.DeviceName, "linux");
            Console.WriteLine($"✓ 已用环境变量自动登录：{user}");
            Cfg.Username = user;
            Cfg.Token = data.Token;
            Cfg.Save();
            Token = data.Token;
            _tokenReady = true;
            return Token;
        }

        throw new InvalidOperationException(
            "还没登录。请执行 aipai login <账号> <密码>，"
            + "或设置环境变量 AIPAI_USER / AIPAI_PASSWORD（容器/服务方式）");
    }

    public async Task<string> LoginAsync(string user, string pass)
    {
        var data = await Api.LoginAsync(user, pass, Cfg.DeviceUid, Cfg.DeviceName, "linux");
        Cfg.Username = user;
        Cfg.Token = data.Token;
        Cfg.Save();
        Token = data.Token;
        _tokenReady = true;
        return data.Token;
    }

    /// <summary>按 序号 / 适配码 / 组网名称 找到目标组网；没找到且是 8 位码则自动加入</summary>
    public async Task<NetworkSummary> ResolveNetworkAsync(string? arg, Action<string>? log = null)
    {
        var token = await EnsureTokenAsync();
        var data = await Api.ListNetworksAsync(token);
        var key = (arg ?? "").Trim();

        if (key == "")
        {
            key = (Environment.GetEnvironmentVariable("AIPAI_NETWORK") ?? "").Trim();
        }

        NetworkSummary? net = null;
        if (key != "")
        {
            if (int.TryParse(key, out var idx) && idx >= 1 && idx <= data.Networks.Count)
            {
                net = data.Networks[idx - 1];
            }
            else
            {
                net = data.Networks.FirstOrDefault(n =>
                        string.Equals(n.Code, key, StringComparison.OrdinalIgnoreCase))
                    ?? data.Networks.FirstOrDefault(n =>
                        string.Equals(n.Name, key, StringComparison.OrdinalIgnoreCase))
                    ?? data.Networks.FirstOrDefault(n =>
                        n.Name.StartsWith(key, StringComparison.OrdinalIgnoreCase));
                if (net == null)
                {
                    if (key.Length != 8)
                    {
                        throw new InvalidOperationException(
                            $"没有找到叫「{key}」的组网；要加入新组网请输入 8 位适配码。");
                    }
                    var joined = await Api.JoinNetworkAsync(key.ToUpperInvariant(), token);
                    net = data.Networks.FirstOrDefault(n => n.Id == joined.Id)
                        ?? new NetworkSummary
                        {
                            Id = joined.Id, Name = joined.Name, Code = joined.Code, Role = joined.Role,
                        };
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
            throw new InvalidOperationException(
                "没有可连接的组网：先创建一个，或用适配码加入一个。");
        }
        Cfg.LastNetworkId = net.Id;
        Cfg.Save();
        return net;
    }

    /// <summary>接入组网（含自动加入、等锚点、启动引擎）</summary>
    public async Task<RoomConfig> ConnectAsync(NetworkSummary net, Action<string>? log = null)
    {
        var token = await EnsureTokenAsync();
        log?.Invoke($"正在接入组网「{net.Name}」…");
        RoomConfig cfg;
        try
        {
            cfg = await WaitAnchorAsync(net.Id, token, log);
        }
        catch (ApiException ex) when (ex.Code == "NOT_FOUND")
        {
            log?.Invoke($"  本机还没加入「{net.Name}」，正在用适配码 {net.Code} 加入…");
            await Api.JoinNetworkAsync(net.Code, token);
            cfg = await WaitAnchorAsync(net.Id, token, log);
        }
        Engine.Start(cfg);
        await Task.Delay(2500);
        if (!Engine.IsRunning)
        {
            throw new InvalidOperationException("组网进程启动失败（可能是旧进程占用或权限不足）。");
        }
        log?.Invoke($"已接入组网「{cfg.Name}」，本机固定IP：{cfg.Ipv4}");
        return cfg;
    }

    public async Task<RoomConfig> ConnectAsync(string? arg, Action<string>? log = null)
        => await ConnectAsync(await ResolveNetworkAsync(arg, log), log);

    public void Disconnect() => Engine.Stop();

    private async Task<RoomConfig> WaitAnchorAsync(int networkId, string token,
        Action<string>? log, int maxSeconds = 150)
    {
        var waited = 0;
        while (true)
        {
            try
            {
                var cfg = await Api.GetNetworkConfigAsync(networkId, token);
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
            log?.Invoke($"  组网锚点启动中，已等待 {waited} 秒…");
        }
    }
}
