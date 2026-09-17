using System.Runtime.InteropServices;

namespace AppiieNet.Client.Services;

/// <summary>组网内某台机器对外共享的一项资源</summary>
public sealed class RemoteShare
{
    public string Host { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int ShareType { get; set; }          // 0=文件夹/磁盘，1=打印机
    public string Name { get; set; } = "";

    public string KindText => ShareType == 1 ? "打印机" : "文件夹";
    public string Unc => $@"\\{Host}\{Name}";
    public string Display => $"{DeviceName}（{Host}） {Name}";
}

/// <summary>
/// 用 Windows 原生 NetShareEnum 枚举远程共享，
/// 比解析 `net view` 的本地化文本可靠（中文/英文系统都能用）。
/// </summary>
public static class RemoteShareScanner
{
    private const uint MaxPreferredLength = 0xFFFFFFFF;
    private const int NerrSuccess = 0;
    private const int ErrorMoreData = 234;
    private const uint StypeDiskTree = 0;
    private const uint StypePrintQ = 1;
    private const uint StypeIpc = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string NetName;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string Remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string? serverName, int level, out IntPtr bufPtr,
        uint prefMaxLen, out uint entriesRead, out uint totalEntries, ref uint resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>枚举 host 上的共享；返回 null 表示这台机器连不上/没权限</summary>
    public static List<RemoteShare>? Enumerate(string host, string deviceName)
        => Enumerate(host, deviceName, out _);

    /// <summary>枚举 host 上的共享，errorCode 回传 Win32 错误码（5=拒绝访问，53/67=找不到网络路径）</summary>
    public static List<RemoteShare>? Enumerate(string host, string deviceName, out int errorCode)
    {
        errorCode = 0;
        var list = new List<RemoteShare>();
        var resume = 0u;
        IntPtr buf = IntPtr.Zero;
        try
        {
            var rc = NetShareEnum(host, 1, out buf, MaxPreferredLength,
                out var read, out _, ref resume);
            if (rc != NerrSuccess && rc != ErrorMoreData)
            {
                errorCode = rc;
                return null;
            }
            var size = Marshal.SizeOf<ShareInfo1>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<ShareInfo1>(IntPtr.Add(buf, (int)(i * size)));
                var name = info.NetName ?? "";
                if (name.Length == 0 || name.EndsWith('$'))     // 跳过 C$ / ADMIN$ 之类
                {
                    continue;
                }
                if (info.Type == StypeIpc || (info.Type != StypeDiskTree && info.Type != StypePrintQ))
                {
                    continue;
                }
                list.Add(new RemoteShare
                {
                    Host = host,
                    DeviceName = deviceName,
                    Name = name,
                    ShareType = (int)info.Type,
                });
            }
            return list;
        }
        catch
        {
            errorCode = -1;
            return null;
        }
        finally
        {
            if (buf != IntPtr.Zero)
            {
                NetApiBufferFree(buf);
            }
        }
    }
}
