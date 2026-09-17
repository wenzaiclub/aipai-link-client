using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AppiieNet.Client.Services;

/// <summary>本机对外共享的一项（文件夹 / 磁盘 / 打印机）。</summary>
public sealed class ShareEntry
{
    public string Kind { get; set; } = "file";     // file / printer
    public string Name { get; set; } = "";         // 共享名
    public string Target { get; set; } = "";       // 本地路径 或 打印机名
    public string Unc { get; set; } = "";          // \\虚拟IP\共享名

    public string KindText => Kind == "printer" ? "打印机" : "文件夹";
    public string Address => string.IsNullOrEmpty(Unc) ? "（连接组网后显示地址）" : Unc;
}

/// <summary>已映射的网络驱动器。</summary>
public sealed class DriveEntry
{
    public string Letter { get; set; } = "";
    public string Unc { get; set; } = "";
    public string Status { get; set; } = "";
    public string Display => $"{Letter}  →  {Unc}";
}

/// <summary>
/// 调用系统 SMB / 打印服务实现「共享文件夹、共享磁盘、共享打印机、映射网络驱动器」。
/// 客户端以管理员身份运行，因此这些操作不需要再弹 UAC。
/// </summary>
public static class ShareService
{
    private const char Sep = '\u001f';

    private const int SHCNE_DRIVEADD = 0x00000100;
    private const int SHCNE_DRIVEDEREMOVED = 0x00000200;
    private const uint SHCNF_PATHW = 0x0005;
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 2;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, string? item2);

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastVolume
    {
        public int Size;          // DWORD dbcv_size
        public short DeviceType;  // WORD  dbcv_devicetype
        public short Pad;
        public int Reserved;      // DWORD dbcv_reserved
        public int UnitMask;      // DWORD dbcv_unitmask
        public short Flags;       // WORD  dbcv_flags
        public short Tail;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam,
        ref DevBroadcastVolume lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>
    /// 让资源管理器立刻刷新盘符列表。
    /// 注意：光用 SHChangeNotify 不会刷新"已经打开着的此电脑窗口"，
    /// 必须广播 WM_DEVICECHANGE 才会即时出现（实测有效）。
    /// </summary>
    public static void NotifyDriveChanged(string letter, bool added)
    {
        try
        {
            var ch = char.ToUpperInvariant(letter.TrimEnd('\\', ':').TrimStart('\\')[0]);
            if (ch < 'A' || ch > 'Z')
            {
                return;
            }
            var root = ch + ":\\";
            var dbv = new DevBroadcastVolume
            {
                Size = Marshal.SizeOf<DevBroadcastVolume>(),
                DeviceType = DBT_DEVTYP_VOLUME,
                Reserved = 0,
                UnitMask = 1 << (ch - 'A'),
                Flags = 0,
            };
            SendMessageTimeout(HWND_BROADCAST, WM_DEVICECHANGE,
                new IntPtr(added ? DBT_DEVICEARRIVAL : DBT_DEVICEREMOVECOMPLETE),
                ref dbv, SMTO_ABORTIFHUNG, 2000, out _);
            // 再补一个 shell 通知，覆盖部分第三方文件管理器
            SHChangeNotify(added ? SHCNE_DRIVEADD : SHCNE_DRIVEDEREMOVED, SHCNF_PATHW, root, null);
        }
        catch
        {
            // 刷新通知失败不影响映射本身
        }
    }

    // 结尾必须带换行，否则会和后面的脚本首行粘在一起
    private const string Prelude =
        "$ErrorActionPreference = 'Stop'\n"
        + "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n"
        + "$OutputEncoding = [Text.Encoding]::UTF8\n";

    /* ---------------- 本机共享 ---------------- */

    /// <summary>
    /// 开启免密共享：本机会创建一个专用共享账号，同时放开来宾/匿名访问，
    /// 这样队友（包括没装本软件的电脑）用 \\虚拟IP\共享名 就能直接进，不用输密码。
    /// 注意：会降低本机的共享安全级别，只建议在可信网络里用。
    /// </summary>
    public static Task<string> EnablePasswordlessSharingAsync(string user, string pass)
        => RunAsync(Prelude + """
$u = $env:APA_USER
$p = $env:APA_PASS
$log = @()

# 1) 专用共享账号
#    注意：这里只用系统自带的 net.exe —— 不依赖 Microsoft.PowerShell.Security /
#    LocalAccounts 这些模块。有些机器上（杀软、环境变量被清理等）模块加载会失败，
#    之前用 New-LocalUser + ConvertTo-SecureString 就会整段报错。
$exists = $false
try { & net user $u 2>$null | Out-Null; if ($LASTEXITCODE -eq 0) { $exists = $true } } catch { }
if ($exists) {
    try { & net user $u $p | Out-Null } catch { }
    $log += '共享账号已更新'
} else {
    try { & net user $u $p /add /comment:"艾派互联免密共享账号" | Out-Null } catch { }
    $log += '已创建共享账号'
}
try { & net user $u /expires:never | Out-Null } catch { }
try { & wmic useraccount where "name='$u'" set PasswordExpires=false | Out-Null } catch { }

# 2) 开启来宾账号（没装本软件的电脑也能直接访问）
try { & net user guest /active:yes | Out-Null } catch { }

# 3) 允许匿名连接使用 Everyone 权限
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name everyoneincludesanonymous -Value 1 -Type DWord
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name LimitBlankPasswordUse -Value 0 -Type DWord

# 4) 本机作为客户端时允许来宾登录（Win10/11 默认阻止，会报“组织安全策略阻止未经身份验证的来宾访问”）
$k = 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters'
if (-not (Test-Path $k)) { New-Item $k -Force | Out-Null }
Set-ItemProperty $k -Name AllowInsecureGuestAuth -Value 1 -Type DWord

# 5) 防火墙放行共享（netsh，同样不依赖 PowerShell 模块）
foreach ($g in @('文件和打印机共享','网络发现','File and Printer Sharing','Network Discovery')) {
    try { & netsh advfirewall firewall set rule group="$g" new enable=yes | Out-Null } catch { }
}
$log += '来宾访问已放开'
$log -join ' / '
""", new Dictionary<string, string> { ["APA_USER"] = user, ["APA_PASS"] = pass });

    /// <summary>关闭免密共享（把来宾/匿名相关设置还原）</summary>
    public static Task<string> DisablePasswordlessSharingAsync()
        => RunAsync(Prelude + """
try { & net user guest /active:no | Out-Null } catch { }
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name everyoneincludesanonymous -Value 0 -Type DWord
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name LimitBlankPasswordUse -Value 1 -Type DWord
$k = 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters'
if (Test-Path $k) { Set-ItemProperty $k -Name AllowInsecureGuestAuth -Value 0 -Type DWord }
Write-Output '已关闭免密共享（共享账号保留，不影响已共享的内容）'
""");

    /// <summary>清掉到某台机器的 SMB 会话/缓存凭据，便于换一种方式重试</summary>
    public static Task<string> ClearServerSessionAsync(string unc)
    {
        var host = unc.TrimStart('\\', '/').Split('\\', '/')[0];
        return RunAsync(Prelude + """
$h = $env:APA_HOST
try { & net use "\\$h" /delete /y 2>$null | Out-Null } catch { }
try { & cmdkey /delete:$h 2>$null | Out-Null } catch { }
Write-Output 'OK'
""", new Dictionary<string, string> { ["APA_HOST"] = host });
    }

    /* ---------------- 扫描组网内队友的共享 ---------------- */

    /// <summary>先快速探一下 445 端口：离线设备直接跳过，不然 SMB 调用会卡几十秒</summary>
    public static async Task<bool> IsSmbReachableAsync(string host, int timeoutMs = 1200)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connect = client.ConnectAsync(host, 445);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs));
            if (done != connect)
            {
                return false;
            }
            await connect;      // 让异常正常抛出
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>扫描一台机器上有哪些共享（文件夹/打印机）。不可达会很快返回。</summary>
    public static async Task<List<RemoteShare>> ScanPeerAsync(
        string host, string deviceName, string user, string pass, int timeoutMs = 5000)
    {
        if (!await IsSmbReachableAsync(host))
        {
            return new List<RemoteShare>();     // 离线 / 防火墙挡着，直接跳过
        }

        var first = await TryEnumerateAsync(host, deviceName, timeoutMs);
        if (first.Shares != null)
        {
            return first.Shares;
        }

        // 只有"拒绝访问"才值得用共享账号重试；网络不通就不折腾了
        if (first.AccessDenied && user != "")
        {
            await RunAsync(Prelude + """
$h = $env:APA_HOST
try { & net use "\\$h\IPC$" /delete /y 2>$null | Out-Null } catch { }
try { & net use "\\$h\IPC$" $env:APA_PASS /user:$env:APA_USER 2>&1 | Out-Null } catch { }
Write-Output 'OK'
""", new Dictionary<string, string>
            {
                ["APA_HOST"] = host,
                ["APA_USER"] = user,
                ["APA_PASS"] = pass,
            });
            var again = await TryEnumerateAsync(host, deviceName, timeoutMs);
            if (again.Shares != null)
            {
                return again.Shares;
            }
        }
        return new List<RemoteShare>();
    }

    private static async Task<(List<RemoteShare>? Shares, bool AccessDenied)> TryEnumerateAsync(
        string host, string deviceName, int timeoutMs)
    {
        try
        {
            var task = Task.Run(() =>
            {
                var found = RemoteShareScanner.Enumerate(host, deviceName, out var code);
                return (Found: found, Code: code);
            });
            var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (done != task)
            {
                return (null, false);        // 超时
            }
            var r = await task;
            return (r.Found, r.Code == 5);   // 5 = ERROR_ACCESS_DENIED
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>连接队友共享的打印机</summary>
    public static async Task<string> ConnectPrinterAsync(string unc)
    {
        var body = "try {\n"
            + "  Add-Printer -ConnectionName '" + unc.Replace("'", "''") + "' -ErrorAction Stop\n"
            + "} catch {\n"
            + "  & rundll32.exe printui.dll,PrintUIEntry /in /q /n '" + unc.Replace("'", "''") + "'\n"
            + "}\nWrite-Output 'OK'";
        var output = await RunInUserSessionAsync(body);
        if (output.Contains("OK", StringComparison.Ordinal))
        {
            return "OK";
        }
        var elevated = await RunAsync(Prelude + """
try {
    Add-Printer -ConnectionName $env:APA_UNC -ErrorAction Stop
} catch {
    & rundll32.exe printui.dll,PrintUIEntry /in /q /n $env:APA_UNC
}
Write-Output 'OK'
""", new Dictionary<string, string> { ["APA_UNC"] = unc });
        return elevated.Contains("OK", StringComparison.Ordinal) ? "OK" : Clean(output);
    }

    public static async Task<List<ShareEntry>> ListSharesAsync()
    {
        var script = Prelude + """
$rows = @()
Get-SmbShare -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Special } |
    ForEach-Object { $rows += ('file' + [char]31 + $_.Name + [char]31 + $_.Path) }
Get-Printer -ErrorAction SilentlyContinue |
    Where-Object { $_.Shared -eq $true } |
    ForEach-Object {
        $sn = $_.ShareName
        if (-not $sn) { $sn = $_.Name }
        $rows += ('printer' + [char]31 + $sn + [char]31 + $_.Name)
    }
$rows
""";
        var output = await RunAsync(script);
        var list = new List<ShareEntry>();
        foreach (var line in SplitLines(output))
        {
            var f = line.Split(Sep);
            if (f.Length < 3)
            {
                continue;
            }
            list.Add(new ShareEntry { Kind = f[0], Name = f[1], Target = f[2] });
        }
        return list;
    }

    public static Task<string> ShareFolderAsync(string path, string name)
        => RunAsync(Prelude + """
$p = $env:APA_PATH
$n = $env:APA_NAME
if (-not (Test-Path -LiteralPath $p)) { throw ('路径不存在：' + $p) }
if (Get-SmbShare -Name $n -ErrorAction SilentlyContinue) { throw ('共享名已存在：' + $n) }
New-SmbShare -Name $n -Path $p -FullAccess 'Everyone' -Description '艾派互联' -ErrorAction Stop | Out-Null
# 整盘共享时不动系统盘的 NTFS 权限，避免降低本机安全性
if ($p -notmatch '^[A-Za-z]:\\?$') {
    try { icacls $p /grant '*S-1-1-0:(OI)(CI)M' | Out-Null } catch { }
}
Write-Output 'OK'
""", new Dictionary<string, string> { ["APA_PATH"] = path, ["APA_NAME"] = name });

    public static Task<string> RemoveShareAsync(ShareEntry item)
        => item.Kind == "printer"
            ? RunAsync(Prelude + """
Set-Printer -Name $env:APA_NAME -Shared $false -ErrorAction Stop
Write-Output 'OK'
""", new Dictionary<string, string> { ["APA_NAME"] = item.Target })
            : RunAsync(Prelude + """
Remove-SmbShare -Name $env:APA_NAME -Force -ErrorAction Stop
Write-Output 'OK'
""", new Dictionary<string, string> { ["APA_NAME"] = item.Name });

    /* ---------------- 打印机 ---------------- */

    public static async Task<List<(string Key, string Display)>> ListPrintersAsync()
    {
        var script = Prelude + """
Get-Printer -ErrorAction SilentlyContinue | ForEach-Object {
    $state = if ($_.Shared) { '已共享' } else { '未共享' }
    Write-Output ($_.Name + [char]31 + ($_.DriverName + ' · ' + $state))
}
""";
        var output = await RunAsync(script);
        var list = new List<(string, string)>();
        foreach (var line in SplitLines(output))
        {
            var f = line.Split(Sep);
            if (f.Length >= 2)
            {
                list.Add((f[0], $"{f[0]}　({f[1]})"));
            }
        }
        return list;
    }

    public static async Task<string> SharePrinterAsync(string printerName, string shareName)
    {
        var output = await RunAsync(Prelude + """
$n = $env:APA_NAME
$sn = $env:APA_SHARE
Set-Service -Name Spooler -StartupType Automatic -ErrorAction SilentlyContinue
Start-Service -Name Spooler -ErrorAction SilentlyContinue
try {
    Set-Printer -Name $n -Shared $true -ShareName $sn -ErrorAction Stop
} catch {
    & rundll32.exe printui.dll,PrintUIEntry /Xs /n $n attributes +shared sharename $sn
}
foreach ($g in @('文件和打印机共享','File and Printer Sharing')) {
    try { Enable-NetFirewallRule -DisplayGroup $g -ErrorAction Stop } catch { }
}
Write-Output 'OK'
""", new Dictionary<string, string> { ["APA_NAME"] = printerName, ["APA_SHARE"] = shareName });
        return output;
    }

    /* ---------------- 映射网络驱动器 ---------------- */

    /// <summary>
    /// 本程序以管理员身份运行，直接映射的盘符属于“管理员会话”，
    /// 资源管理器（普通用户会话）里看不到。所以统一放到登录用户会话里执行。
    /// </summary>
    private static async Task<string> RunInUserSessionAsync(string body, int timeoutMs = 20000)
    {
        var inner = TempPath(".ps1");
        var outFile = TempPath(".txt");
        try
        {
            var script = "$ErrorActionPreference='Stop'\n"
                + "[Console]::OutputEncoding=[Text.Encoding]::UTF8\n"
                + "& {\n" + body + "\n} *>&1 | Out-File -FilePath '" + outFile.Replace("'", "''")
                + "' -Encoding UTF8\n";
            await File.WriteAllTextAsync(inner, script, new UTF8Encoding(true));

            var args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + inner + "\"";
            if (!await UserSessionLauncher.TryRunAsync("powershell.exe", args, timeoutMs + 8000))
            {
                return "";
            }

            var waited = 0;
            while (waited < timeoutMs && !File.Exists(outFile))
            {
                await Task.Delay(300);
                waited += 300;
            }
            await Task.Delay(400);
            return File.Exists(outFile) ? (await File.ReadAllTextAsync(outFile)).Trim() : "";
        }
        finally
        {
            foreach (var f in new[] { inner, outFile })
            {
                try
                {
                    File.Delete(f);
                }
                catch
                {
                    // 清理失败不影响功能
                }
            }
        }
    }

    /// <summary>让管理员会话也能看到普通用户的映射（设置后重启一次生效）</summary>
    public static void EnsureLinkedConnections()
    {
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            if (k != null && !Equals(k.GetValue("EnableLinkedConnections"), 1))
            {
                k.SetValue("EnableLinkedConnections", 1, RegistryValueKind.DWord);
            }
        }
        catch
        {
            // 没权限就跳过
        }
    }

    public static async Task<List<DriveEntry>> ListMappedDrivesAsync()
    {
        var list = new List<DriveEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var body = "Get-SmbMapping -ErrorAction SilentlyContinue | Where-Object { $_.RemotePath }"
            + " | ForEach-Object { Write-Output ($_.LocalPath + [char]31 + $_.RemotePath + [char]31"
            + " + [string]$_.Status) }";
        var fromUser = await RunInUserSessionAsync(body);
        var fromElevated = await RunAsync(Prelude + """
Get-SmbMapping -ErrorAction SilentlyContinue | Where-Object { $_.RemotePath } | ForEach-Object {
    Write-Output ($_.LocalPath + [char]31 + $_.RemotePath + [char]31 + [string]$_.Status)
}
""");

        foreach (var line in SplitLines(fromUser + "\n" + fromElevated))
        {
            var f = line.Split(Sep);
            if (f.Length >= 2 && seen.Add(f[0]))
            {
                list.Add(new DriveEntry
                {
                    Letter = f[0],
                    Unc = f[1],
                    Status = f.Length > 2 ? f[2] : "",
                });
            }
        }
        list.Sort((a, b) => string.Compare(a.Letter, b.Letter, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public static async Task<string> MapDriveAsync(string unc, string letter, string user, string pass)
    {
        EnsureLinkedConnections();
        var auth = "";
        if (user != "")
        {
            auth = " -UserName '" + user.Replace("'", "''") + "' -Password '"
                + pass.Replace("'", "''") + "'";
        }
        var body = "New-SmbMapping -RemotePath '" + unc.Replace("'", "''")
            + "' -LocalPath '" + letter.Replace("'", "''") + "'" + auth
            + " -Persistent:$true -ErrorAction Stop | Out-Null\nWrite-Output 'OK'";

        var output = await RunInUserSessionAsync(body);
        if (output.Contains("OK", StringComparison.Ordinal))
        {
            NotifyDriveChanged(letter, true);
            return "OK";
        }
        if (output == "")
        {
            // 拿不到用户会话令牌时的兜底：在当前（管理员）会话里映射
            await RunAsync(Prelude + """
$unc = $env:APA_UNC
$letter = $env:APA_LETTER
if ($env:APA_USER -ne '') {
    New-SmbMapping -RemotePath $unc -LocalPath $letter -UserName $env:APA_USER -Password $env:APA_PASS -Persistent:$true -ErrorAction Stop | Out-Null
} else {
    New-SmbMapping -RemotePath $unc -LocalPath $letter -Persistent:$true -ErrorAction Stop | Out-Null
}
Write-Output 'OK'
""", new Dictionary<string, string>
            {
                ["APA_UNC"] = unc,
                ["APA_LETTER"] = letter,
                ["APA_USER"] = user,
                ["APA_PASS"] = pass,
            });
            NotifyDriveChanged(letter, true);
            return "ELEVATED";
        }
        throw new InvalidOperationException(Clean(output));
    }

    public static async Task<string> UnmapDriveAsync(string letter)
    {
        var l = letter.Replace("'", "''");
        var body = "try { Remove-SmbMapping -LocalPath '" + l
            + "' -Force -UpdateProfile -ErrorAction Stop | Out-Null } catch { }\n"
            + "if (Get-SmbMapping -LocalPath '" + l + "' -ErrorAction SilentlyContinue) {\n"
            + "  cmd /c 'net use " + l + " /delete /y' | Out-Null\n}\nWrite-Output 'OK'";
        await RunInUserSessionAsync(body);
        await RunAsync(Prelude + """
$l = $env:APA_LETTER
try { Remove-SmbMapping -LocalPath $l -Force -UpdateProfile -ErrorAction Stop | Out-Null } catch { }
if (Get-SmbMapping -LocalPath $l -ErrorAction SilentlyContinue) {
    cmd /c "net use $l /delete /y" | Out-Null
}
        Write-Output 'OK'
""", new Dictionary<string, string> { ["APA_LETTER"] = letter });
        NotifyDriveChanged(letter, false);
        return "OK";
    }

    private static string TempPath(string ext)
        => Path.Combine(Path.GetTempPath(), "aipai-" + Guid.NewGuid().ToString("N") + ext);

    /// <summary>
    /// 依次尝试三种身份映射：免密共享账号 → 匿名（当前登录用户）→ guest。
    /// Linux / 飞牛 / 群晖 上的 Samba 共享通常只认 guest，所以最后这条很关键。
    /// </summary>
    public static async Task<string> MapWithFallbackAsync(string unc, string letter,
        string user, string pass)
    {
        var errors = new List<string>();
        if (!string.IsNullOrEmpty(user))
        {
            try
            {
                return await MapDriveAsync(unc, letter, user, pass);
            }
            catch (Exception ex)
            {
                errors.Add($"用账号 {user} 失败：{ex.Message}");
                await ClearServerSessionAsync(unc);
            }
        }
        try
        {
            return await MapDriveAsync(unc, letter, "", "");
        }
        catch (Exception ex)
        {
            errors.Add("匿名访问失败：" + ex.Message);
            await ClearServerSessionAsync(unc);
        }
        try
        {
            // Linux/NAS 的 Samba 共享一般用 guest 免密
            return await MapDriveAsync(unc, letter, "guest", "");
        }
        catch (Exception ex)
        {
            errors.Add("用 guest 失败：" + ex.Message);
        }
        throw new InvalidOperationException(string.Join("\n", errors));
    }

    /// <summary>
    /// 把所有已映射的盘符重新广播一次卷到达事件。
    /// 已经打开的"此电脑"窗口不会自己刷新，这个操作能让它立刻显示出来。
    /// </summary>
    public static async Task<int> RefreshExplorerAsync()
    {
        var list = await ListMappedDrivesAsync();
        var n = 0;
        foreach (var d in list)
        {
            if (!string.IsNullOrWhiteSpace(d.Letter))
            {
                NotifyDriveChanged(d.Letter, true);
                n++;
            }
        }
        return n;
    }

    /* ---------------- 本机信息 ---------------- */

    /// <summary>可共享的本地磁盘（固定盘 + 移动盘）。</summary>
    public static List<(string Key, string Display)> LocalDrives()
    {
        var list = new List<(string, string)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)
            {
                continue;
            }
            string info;
            try
            {
                info = d.IsReady
                    ? $"{DriveTypeText(d)} · 可用 {Gb(d.AvailableFreeSpace)} / {Gb(d.TotalSize)}"
                    : "未就绪";
                if (d.IsReady && !string.IsNullOrEmpty(d.VolumeLabel))
                {
                    info = d.VolumeLabel + " · " + info;
                }
            }
            catch
            {
                info = d.DriveType == DriveType.Removable ? "可移动磁盘" : "本地磁盘";
            }
            list.Add((d.Name, $"{d.Name}　{info}"));
        }
        return list;
    }

    /// <summary>尚未占用的盘符，用于映射网络驱动器。</summary>
    public static List<string> FreeDriveLetters()
    {
        var used = new HashSet<string>(
            DriveInfo.GetDrives().Select(d => d.Name.Substring(0, 2).ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);
        return Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(c => (char)c + ":")
            .Where(l => !used.Contains(l))
            .ToList();
    }

    /// <summary>把路径处理成合法的共享名（默认取磁盘符或文件夹名）。</summary>
    public static string SuggestShareName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var raw = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(raw))
        {
            raw = trimmed.TrimEnd(':');
        }
        var sb = new StringBuilder();
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                sb.Append(c);
            }
        }
        var name = sb.ToString();
        return string.IsNullOrEmpty(name) ? "AipaiShare" : name[..Math.Min(12, name.Length)];
    }

    private static string DriveTypeText(DriveInfo d)
        => d.DriveType == DriveType.Removable ? "可移动磁盘" : "本地磁盘";

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.#") + " GB";

    /* ---------------- 执行 ---------------- */

    public static async Task<string> RunAsync(string script, Dictionary<string, string>? vars = null)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "aipai-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            // 带 BOM 的 UTF-8，避免中文路径被 PowerShell 5.1 读花
            await File.WriteAllTextAsync(tmp, script, new UTF8Encoding(true));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + tmp + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (vars != null)
            {
                foreach (var kv in vars)
                {
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
                }
            }

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 PowerShell");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var stdout = (await outTask).Trim();
            var stderr = (await errTask).Trim();
            if (p.ExitCode != 0 && !stdout.Contains("OK", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(stderr) ? "操作失败（退出码 " + p.ExitCode + "）" : Clean(stderr));
            }
            return stdout;
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // 临时文件清理失败不影响功能
            }
        }
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim('\r', ' ', '\t'))
            .Where(l => l.Length > 0);

    /// <summary>去掉 PowerShell 的调用栈噪音，只留真正的错误原因。</summary>
    private static string Clean(string s)
    {
        var all = s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var kept = all.Where(l =>
            !l.StartsWith("At ", StringComparison.Ordinal)
            && !l.StartsWith("+", StringComparison.Ordinal)
            && !l.StartsWith("~~~", StringComparison.Ordinal)
            && !l.StartsWith("-", StringComparison.Ordinal)
            && !l.StartsWith("CategoryInfo", StringComparison.Ordinal)
            && !l.StartsWith("FullyQualifiedErrorId", StringComparison.Ordinal)
            && !l.StartsWith("所在位置", StringComparison.Ordinal)
            && !l.StartsWith("    +", StringComparison.Ordinal)).ToList();
        if (kept.Count == 0)
        {
            kept = all;
        }
        return string.Join("\n", kept.Take(3));
    }
}
