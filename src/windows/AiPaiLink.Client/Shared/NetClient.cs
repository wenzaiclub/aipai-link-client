using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace AppiieNet.Client.Services;

/// <summary>
/// 统一的 HTTP 出口，专门兜住「系统代理是死的」这个坑。
///
/// 背景：不少用户挂着本地代理（127.0.0.1:7890 这类）。代理软件一关，Windows 的系统代理设置
/// 常常还指着那个端口，.NET 会把之后所有请求继续送去那里，于是报
/// 「由于目标计算机积极拒绝，无法连接。(127.0.0.1:xxxx)」——登录、刷新、连组网全部失败，
/// 用户看到的现象就是"软件坏了"。
///
/// 这里的做法：一旦发现请求是因为代理连不上而失败，就改成直连重试，并在本次运行内一直用直连。
/// </summary>
public static class NetClient
{
    private static int _directOnly;

    /// <summary>本次运行是否已经切成直连</summary>
    public static bool DirectOnly => Volatile.Read(ref _directOnly) == 1;

    /// <summary>按当前策略建一个 HttpClient</summary>
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
        if (DirectOnly)
        {
            // 明确不走代理。注意不能只把 Proxy 置空——那等于"用系统默认代理"。
            handler.UseProxy = false;
            handler.Proxy = null;
        }
        else
        {
            handler.UseProxy = true;
            handler.Proxy = WebRequest.DefaultWebProxy;
        }

        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>这个异常像不像「代理连不上」造成的</summary>
    public static bool LooksLikeProxyFailure(Exception? ex)
    {
        if (DirectOnly || ex == null)
        {
            return false;
        }

        // 代理端口没人监听时，内层通常是 SocketException（连接被拒/超时）
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException)
            {
                return true;
            }
        }

        var msg = ex.Message;
        return msg.Contains("积极拒绝", StringComparison.Ordinal)
            || msg.Contains("refused", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>切成直连；返回 true 表示这次是第一次切</summary>
    public static bool SwitchToDirect()
    {
        if (Interlocked.Exchange(ref _directOnly, 1) == 0)
        {
            AppLog.Client("系统代理连不上，本次运行自动改为直连");
            return true;
        }

        return false;
    }
}
