using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AppiieNet.Client.Services;

/// <summary>
/// 用 explorer.exe 的令牌启动进程（中等完整性 = 非提权），
/// 也就是“登录用户自己的那个会话”。
///
/// 为什么需要它：本程序以管理员运行，映射的网络驱动器只属于管理员会话，
/// 资源管理器看不到；只有把映射命令放到用户会话里执行，"此电脑"里才会出现盘符。
/// </summary>
public static class UserSessionLauncher
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs,
        int impersonation, int type, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags,
        string? appName, StringBuilder commandLine, uint creationFlags, IntPtr env,
        string? currentDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    /// <summary>在登录用户（非提权）会话里执行命令。返回 false 表示没找到可用的用户令牌。</summary>
    public static async Task<bool> TryRunAsync(string exe, string args, int timeoutMs = 20000)
    {
        var explorer = FindUserExplorer();
        if (explorer == null)
        {
            return false;
        }

        IntPtr hProcess = IntPtr.Zero, hToken = IntPtr.Zero, hNewToken = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        try
        {
            hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, explorer.Id);
            if (hProcess == IntPtr.Zero
                || !OpenProcessToken(hProcess, TOKEN_DUPLICATE | TOKEN_QUERY, out hToken))
            {
                return false;
            }
            if (!DuplicateTokenEx(hToken,
                    TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY
                        | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID,
                    IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hNewToken))
            {
                return false;
            }

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var cmdLine = new StringBuilder("\"" + exe + "\" " + args);
            if (!CreateProcessWithTokenW(hNewToken, 0, null, cmdLine, CREATE_NO_WINDOW,
                    IntPtr.Zero, null, ref si, out pi))
            {
                return false;
            }

            var waited = await Task.Run(() =>
                WaitForSingleObject(pi.hProcess, (uint)Math.Min(timeoutMs, 60000)));
            return waited != 0x102; // WAIT_TIMEOUT
        }
        finally
        {
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (hNewToken != IntPtr.Zero) CloseHandle(hNewToken);
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            explorer.Dispose();
        }
    }

    /// <summary>当前会话里那个普通（非提权）的 explorer.exe</summary>
    private static Process? FindUserExplorer()
    {
        var mySession = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            if (p.SessionId == mySession)
            {
                return p;
            }
            p.Dispose();
        }
        return null;
    }
}
