using System.Net;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using AppiieNet.Client.Services;

namespace AppiieNet.Linux.Services;

/// <summary>
/// 网页管理面板：浏览器打开就能看状态、连/断组网。
/// 隧道本身跑在这台机器上（NAS/服务器），网页只负责管理。
/// </summary>
public sealed class WebPanel
{
    private readonly ClientSession _s;
    private string? _sid;
    private readonly List<string> _log = new();
    private readonly object _logGate = new();
    private const int MaxLogLines = 500;

    public WebPanel(ClientSession session)
    {
        _s = session;
        LoadPanelLog();
    }

    public async Task RunAsync(string host, int port)
    {
        var listener = new HttpListener();
        var prefixHost = host is "0.0.0.0" or "*" ? "*" : host;
        listener.Prefixes.Add($"http://{prefixHost}:{port}/");
        listener.Start();
        Console.WriteLine($"✓ 网页管理面板已启动：http://{(host is "0.0.0.0" or "*" ? "服务器IP" : host)}:{port}/");
        Console.WriteLine("  用浏览器打开上面地址，登录你的艾派账号即可管理组网。");

        // 服务方式启动时自动接入（AIPAI_AUTOCONNECT 不为 0）
        if (Environment.GetEnvironmentVariable("AIPAI_AUTOCONNECT") != "0")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    AddLog("开机自动接入中…");
                    var cfg = await _s.ConnectAsync((string?)null, AddLog);
                    AddLog($"已接入「{cfg.Name}」，本机固定IP {cfg.Ipv4}");
                }
                catch (Exception ex)
                {
                    AddLog("自动接入失败：" + ex.Message);
                }
            });
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _s.Disconnect();
        // 被 kill / systemctl stop 时也要把组网引擎一起关掉，避免残留进程
        PosixSignalRegistration? sigterm = null;
        try
        {
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ =>
            {
                _s.Disconnect();
                Environment.Exit(0);
            });
        }
        catch
        {
            // 个别平台不支持，忽略
        }

        while (!cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
        _s.Disconnect();
        listener.Stop();
        sigterm?.Dispose();
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path == "")
            {
                await WriteAsync(ctx, 200, "text/html; charset=utf-8", Page);
                return;
            }
            if (path == "/api/login")
            {
                await LoginAsync(ctx);
                return;
            }
            if (!Authorized(ctx))
            {
                await JsonAsync(ctx, 401, new { ok = false, error = "请先登录" });
                return;
            }
            switch (path)
            {
                case "/api/state":
                    await JsonAsync(ctx, 200, await StateAsync());
                    return;
                case "/api/logs":
                    await JsonAsync(ctx, 200, Logs(ctx));
                    return;
                case "/api/connect":
                    await ConnectAsync(ctx);
                    return;
                case "/api/disconnect":
                    _s.Disconnect();
                    AddLog("已断开组网");
                    await JsonAsync(ctx, 200, new { ok = true });
                    return;
                case "/api/logout":
                    _s.Disconnect();
                    AddLog("退出登录");
                    _sid = null;
                    await JsonAsync(ctx, 200, new { ok = true });
                    return;
                case "/api/create":
                    await CreateOrJoinAsync(ctx, true);
                    return;
                case "/api/join":
                    await CreateOrJoinAsync(ctx, false);
                    return;
                default:
                    await JsonAsync(ctx, 404, new { ok = false, error = "接口不存在" });
                    return;
            }
        }
        catch (Exception ex)
        {
            try
            {
                await JsonAsync(ctx, 500, new { ok = false, error = ex.Message });
            }
            catch
            {
                // 响应已发出
            }
        }
    }

    private bool Authorized(HttpListenerContext ctx)
    {
        if (Environment.GetEnvironmentVariable("AIPAI_WEB_NOAUTH") == "1")
        {
            return true;
        }
        if (string.IsNullOrEmpty(_sid))
        {
            return false;
        }
        var cookie = ctx.Request.Cookies["aipai_sid"]?.Value;
        return cookie == _sid;
    }

    private async Task LoginAsync(HttpListenerContext ctx)
    {
        var body = await ReadJsonAsync(ctx);
        var user = body.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
        var pass = body.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
        if (user == "" || pass == "")
        {
            await JsonAsync(ctx, 200, new { ok = false, error = "请输入账号和密码" });
            return;
        }
        try
        {
            await _s.LoginAsync(user, pass);
            _sid = Guid.NewGuid().ToString("N");
            ctx.Response.AppendHeader("Set-Cookie", $"aipai_sid={_sid}; Path=/; HttpOnly; SameSite=Lax");
            AddLog($"登录成功：{_s.Cfg.Username}");
            await JsonAsync(ctx, 200, new { ok = true, user = _s.Cfg.Username });
        }
        catch (ApiException ex)
        {
            AddLog($"登录失败：{user}（{ex.Message}）");
            await JsonAsync(ctx, 200, new { ok = false, error = ex.Message });
        }
    }

    private async Task CreateOrJoinAsync(HttpListenerContext ctx, bool create)
    {
        var body = await ReadJsonAsync(ctx);
        var key = create
            ? (body.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
            : (body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "");
        if (key.Trim() == "")
        {
            await JsonAsync(ctx, 200, new { ok = false, error = create ? "请输入组网名称" : "请输入适配码" });
            return;
        }
        try
        {
            var token = await _s.EnsureTokenAsync();
            if (create)
            {
                await _s.Api.CreateNetworkAsync(key.Trim(), "", token);
                AddLog($"创建组网：{key.Trim()}");
            }
            else
            {
                await _s.Api.JoinNetworkAsync(key.Trim().ToUpperInvariant(), token);
                AddLog($"加入组网：适配码 {key.Trim().ToUpperInvariant()}");
            }
            await JsonAsync(ctx, 200, new { ok = true });
        }
        catch (ApiException ex)
        {
            AddLog("✗ " + ex.Message);
            await JsonAsync(ctx, 200, new { ok = false, error = ex.Message });
        }
    }

    private async Task ConnectAsync(HttpListenerContext ctx)
    {
        var body = await ReadJsonAsync(ctx);
        var key = body.TryGetProperty("network", out var n) ? n.GetString() : null;
        try
        {
            if (!Environment.IsPrivilegedProcess)
            {
                await JsonAsync(ctx, 200, new { ok = false, error = "服务没有 root 权限，无法创建虚拟网卡" });
                return;
            }
            await _s.ConnectAsync(key, text => AddLog(text));
            AddLog("✓ 组网已接入");
            await JsonAsync(ctx, 200, new { ok = true });
        }
        catch (Exception ex)
        {
            AddLog("✗ " + ex.Message);
            await JsonAsync(ctx, 200, new { ok = false, error = ex.Message });
        }
    }

    private void AddLog(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}";
        lock (_logGate)
        {
            _log.Add(line);
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
        }
        // 同时落盘，重启面板之后还能翻到之前的记录
        try
        {
            File.AppendAllText(PanelLogPath(), line + "\n");
        }
        catch
        {
            // 写不进去不影响运行
        }
    }

    /// <summary>面板日志文件（和引擎日志放同一个目录）</summary>
    private static string PanelLogPath()
    {
        var dir = Path.GetDirectoryName(LinuxEngine.LogPath()) ?? "/tmp";
        return Path.Combine(dir, "panel.log");
    }

    /// <summary>启动时把上次的日志读回来</summary>
    private void LoadPanelLog()
    {
        try
        {
            var path = PanelLogPath();
            if (!File.Exists(path))
            {
                return;
            }
            // 日志文件太大就只留尾巴，别把 NAS 的盘写满
            var fi = new FileInfo(path);
            if (fi.Length > 2 * 1024 * 1024)
            {
                var keep = File.ReadLines(path).TakeLast(MaxLogLines).ToArray();
                File.WriteAllLines(path, keep);
            }
            var lines = File.ReadLines(path).TakeLast(MaxLogLines).ToArray();
            lock (_logGate)
            {
                _log.Clear();
                _log.AddRange(lines);
            }
        }
        catch
        {
            // 读不到就当没有历史日志
        }
    }

    /// <summary>取文件末尾若干行，不整文件读，日志大了也不会卡</summary>
    private static string[] Tail(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, 256 * 1024);
            var partial = take < fs.Length;
            fs.Seek(-take, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n');
            if (partial && lines.Length > 1)
            {
                // 从中间切开的第一行多半是半截，丢掉
                lines = lines.Skip(1).ToArray();
            }
            lines = lines.Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            return lines.Length <= maxLines ? lines : lines[^maxLines..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>日志接口：source=client 看面板操作，source=engine 看组网引擎</summary>
    private object Logs(HttpListenerContext ctx)
    {
        var source = (ctx.Request.QueryString["source"] ?? "client").ToLowerInvariant();
        var lines = 300;
        if (int.TryParse(ctx.Request.QueryString["lines"], out var n))
        {
            lines = Math.Clamp(n, 20, 2000);
        }

        if (source == "engine")
        {
            var path = LinuxEngine.LogPath();
            return new
            {
                ok = true,
                source = "engine",
                path,
                lines = Tail(path, lines),
            };
        }

        string[] arr;
        lock (_logGate)
        {
            arr = _log.TakeLast(lines).ToArray();
        }
        return new { ok = true, source = "client", path = PanelLogPath(), lines = arr };
    }

    private async Task<object> StateAsync()
    {
        string username = "", level = "", expire = "";
        var networks = new List<object>();
        var peers = new List<object>();
        var myIp = "";
        var nat = "";

        try
        {
            var token = await _s.EnsureTokenAsync();
            var me = await _s.Api.MeAsync(token);
            username = me.User.Username;
            level = me.User.Level?.Name ?? me.User.Plan;
            expire = string.IsNullOrEmpty(me.User.Level?.ExpireAt) ? "永久" : me.User.Level!.ExpireAt!;
            var list = await _s.Api.ListNetworksAsync(token);
            networks.AddRange(list.Networks.Select(x => (object)new
            {
                x.Id,
                x.Name,
                x.Code,
                role = x.RoleText,
                ip = string.IsNullOrWhiteSpace(x.DeviceIpv4) ? x.Ipv4 : x.DeviceIpv4,
                x.ServerLocation,
            }));
        }
        catch (Exception ex)
        {
            AddLog("读取账号信息失败：" + ex.Message);
        }

        if (_s.Engine.IsRunning)
        {
            var info = await _s.Engine.GetLocalInfoAsync();
            if (info != null)
            {
                myIp = info.Ipv4;
                nat = info.NatType;
            }
            var list = await _s.Engine.PeersAsync();
            peers.AddRange(list.Where(p => !p.IsLocal).Select(p => (object)new
            {
                host = p.Host,
                ip = p.Ip,
                link = p.LinkText,
                p.Latency,
                p.Loss,
                nat = p.NatType,
            }));
        }

        string[] logs;
        lock (_logGate)
        {
            logs = _log.TakeLast(200).ToArray();
        }
        return new
        {
            ok = true,
            loggedIn = username != "",
            user = new { username, level, expire },
            connected = _s.Engine.IsRunning,
            myIp,
            nat,
            networks,
            peers,
            logs,
        };
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                return JsonDocument.Parse("{}").RootElement;
            }
            return JsonDocument.Parse(text).RootElement;
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private static async Task JsonAsync(HttpListenerContext ctx, int status, object data)
    {
        var json = JsonSerializer.Serialize(data);
        await WriteAsync(ctx, status, "application/json; charset=utf-8", json);
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    /// <summary>页面从内嵌资源读取（web/index.html）</summary>
    private static readonly string Page = LoadPage();

    private static string LoadPage()
    {
        try
        {
            var asm = typeof(WebPanel).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                return "<h1>页面资源缺失</h1>";
            }
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return "<h1>页面加载失败：" + ex.Message + "</h1>";
        }
    }
}
