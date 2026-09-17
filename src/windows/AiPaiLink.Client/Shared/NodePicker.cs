using System.Diagnostics;
using System.Net.Sockets;
using AppiieNet.Client.Models;

namespace AppiieNet.Client.Services;

/// <summary>
/// 按延迟挑节点：对每个节点做 TCP 握手，用握手耗时当延迟，取最快的那个。
///
/// 为什么不用 ping：客户端不一定有权限发 ICMP，很多网络也把 ICMP 丢了；
/// TCP 握手是真实会走的路径，端口没人监听时的 RST 返回同样能测出往返时间。
/// </summary>
public static class NodePicker
{
    public sealed record Pick(string Id, string Name, string Location, int LatencyMs)
    {
        public string Text => LatencyMs > 0
            ? $"{Name}（{LatencyMs}ms）"
            : $"{Name}（延迟未测出）";
    }

    /// <summary>所有节点并发测一遍，返回延迟最低的。节点为空时返回 null。</summary>
    public static async Task<Pick?> PickFastestAsync(IEnumerable<NodeInfo>? nodes, CancellationToken ct = default)
    {
        var list = (nodes ?? Enumerable.Empty<NodeInfo>())
            .Where(n => !string.IsNullOrWhiteSpace(n.Host))
            .ToList();
        if (list.Count == 0) { return null; }

        var tasks = list.Select(async n => new Pick(n.Id, n.Name, n.Location, await MeasureAsync(n, ct)));
        var results = await Task.WhenAll(tasks);

        return results
            .OrderBy(r => r.LatencyMs <= 0 ? int.MaxValue : r.LatencyMs)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .First();
    }

    /// <summary>单个节点的 TCP 握手耗时（毫秒）；测不到返回 -1。测两次取快的。</summary>
    public static async Task<int> MeasureAsync(NodeInfo node, CancellationToken ct = default)
    {
        var port = node.ProbePort > 0 ? node.ProbePort : 443;
        var best = -1;
        for (var i = 0; i < 2; i++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(1500);
                await client.ConnectAsync(node.Host, port, timeout.Token);
                sw.Stop();
                client.Close();
            }
            catch (SocketException ex)
            {
                sw.Stop();
                // 端口没人监听会立刻回 RST，这也是有效的往返时间；其它错误不算
                if (ex.SocketErrorCode != SocketError.ConnectionRefused) { continue; }
            }
            catch (OperationCanceledException) { continue; }
            catch { continue; }

            var ms = (int)sw.Elapsed.TotalMilliseconds;
            if (ms > 0 && (best < 0 || ms < best)) { best = ms; }
        }
        return best;
    }
}
