using System.Diagnostics;

namespace AppiieNet.Linux.Services;

public sealed class LocalShare
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool ReadOnly { get; set; }
    public string Unc { get; set; } = "";
}

public sealed class PeerShare
{
    public string Host { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsPrinter { get; set; }
    public string KindText => IsPrinter ? "打印机" : "文件夹";
    public string Unc => $@"\\{Host}\{Name}";
}

public sealed class MountedShare
{
    public string Unc { get; set; } = "";
    public string Point { get; set; } = "";
}

/// <summary>
/// Linux 端的共享能力：用 Samba 共享本机目录、用 cifs 挂载队友共享、用 CUPS 共享打印机。
/// 组网内全端口互通，所以共享地址直接用组网虚拟 IP（\\10.x.x.x\名字）。
/// </summary>
public static class LinuxShare
{
    private const string ConfPath = "/etc/samba/aipai.conf";
    private const string IncludeLine = "include = /etc/samba/aipai.conf";

    /* ---------------- 执行命令 ---------------- */

    public static async Task<(int Code, string Out, string Err)> RunAsync(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法执行命令");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, (await outTask).Trim(), (await errTask).Trim());
    }

    /// <summary>bash 单引号安全转义</summary>
    public static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static async Task<bool> IsRootAsync()
        => Environment.IsPrivilegedProcess;

    /* ---------------- Samba 环境 ---------------- */

    public static async Task<bool> HasSambaAsync()
    {
        var r = await RunAsync("command -v smbd >/dev/null 2>&1 && echo yes || echo no");
        return r.Out.Trim() == "yes";
    }

    /// <summary>没装就装 Samba（含 smbclient / cifs-utils）</summary>
    public static async Task<string> EnsureSambaAsync(Action<string>? log = null)
    {
        if (await HasSambaAsync())
        {
            return "已安装";
        }
        log?.Invoke("正在安装 Samba（文件共享），第一次会慢一点…");
        var r = await RunAsync(
            "export DEBIAN_FRONTEND=noninteractive; "
            + "apt-get update -qq && apt-get install -y -qq samba cifs-utils 2>&1 | tail -3");
        if (!await HasSambaAsync())
        {
            throw new InvalidOperationException(
                "安装 Samba 失败，请手动执行：sudo apt install -y samba cifs-utils\n" + r.Out + r.Err);
        }
        // 允许免密（来宾）访问
        await RunAsync(
            "grep -q 'aipai.conf' /etc/samba/smb.conf || "
            + $"printf '\\n# 艾派互联共享\\n{IncludeLine}\\n' >> /etc/samba/smb.conf; "
            + "grep -q 'map to guest' /etc/samba/smb.conf || "
            + "sed -i '/\\[global\\]/a\\   map to guest = bad user' /etc/samba/smb.conf");
        return "已安装并初始化";
    }

    private static async Task EnsureConfAsync()
    {
        await RunAsync($"touch {ConfPath}; [ -f {ConfPath} ] || echo '' > {ConfPath}");
    }

    private static async Task RestartSmbdAsync()
    {
        await RunAsync("systemctl restart smbd 2>/dev/null || systemctl restart smb 2>/dev/null || "
            + "service smbd restart 2>/dev/null || true");
    }

    /* ---------------- 本机共享 ---------------- */

    public static async Task<List<LocalShare>> ListAsync(string myIp)
    {
        var list = new List<LocalShare>();
        var r = await RunAsync($"sed -n 's/^\\[\\(.*\\)\\]/\\1/p' {ConfPath} 2>/dev/null");
        var names = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        foreach (var name in names)
        {
            var p = await RunAsync(
                $"awk -F= '/^\\[/{{s=($0==\"[{name}]\")}} s&&/^ *path *=/{{print $2; exit}}' {ConfPath}");
            var ro = await RunAsync(
                $"awk '/^\\[/{{s=($0==\"[{name}]\")}} s&&/read only *= *yes/{{print \"yes\"; exit}}' {ConfPath}");
            list.Add(new LocalShare
            {
                Name = name,
                Path = p.Out.Trim(),
                ReadOnly = ro.Out.Trim() == "yes",
                Unc = string.IsNullOrEmpty(myIp) ? $@"\\<虚拟IP>\{name}" : $@"\\{myIp}\{name}",
            });
        }
        return list;
    }

    public static async Task AddAsync(string path, string name, bool readOnly, string myIp)
    {
        if (path.Length == 0 || !Directory.Exists(path))
        {
            throw new InvalidOperationException($"目录不存在：{path}");
        }
        await EnsureConfAsync();
        name = name.Trim();
        if (name.Length == 0)
        {
            name = Path.GetFileName(path.TrimEnd('/'));
            if (name.Length == 0)
            {
                name = "share";
            }
        }
        // 已有的同名先删掉，实现"覆盖配置"
        await RemoveAsync(name, restart: false, ignoreMissing: true);

        var owner = (await RunAsync("id -un")).Out.Trim();
        var section =
            $"\n[{name}]\n"
            + $"   comment = 艾派互联共享\n"
            + $"   path = {path}\n"
            + $"   browseable = yes\n"
            + $"   read only = {(readOnly ? "yes" : "no")}\n"
            + $"   guest ok = yes\n"
            + $"   force user = {owner}\n"
            + $"   create mask = 0666\n"
            + $"   directory mask = 0777\n";
        await RunAsync($"printf %s {Q(section)} >> {ConfPath}");
        await RunAsync($"chmod -R a+rx {Q(path)} 2>/dev/null; "
            + (readOnly ? "" : $"chmod -R a+rwX {Q(path)} 2>/dev/null; ") + "true");
        await RestartSmbdAsync();
    }

    public static async Task RemoveAsync(string name, bool restart = true, bool ignoreMissing = false)
    {
        await EnsureConfAsync();
        var before = (await RunAsync($"grep -c '^\\[{name}\\]' {ConfPath} 2>/dev/null")).Out.Trim();
        if (before == "0" && !ignoreMissing)
        {
            throw new InvalidOperationException($"没有叫「{name}」的共享");
        }
        // 删掉这一段（从 [name] 到下一个 [ 或文件尾）
        await RunAsync(
            $"awk -v n='[{name}]' 'BEGIN{{skip=0}} $0==n{{skip=1;next}} "
            + "/^\\[/{{skip=0}} !skip{{print}}' "
            + $"{ConfPath} > {ConfPath}.tmp && mv {ConfPath}.tmp {ConfPath}");
        if (restart)
        {
            await RestartSmbdAsync();
        }
    }

    /* ---------------- 扫描队友共享 ---------------- */

    public static async Task<List<PeerShare>> ScanAsync(IEnumerable<string> hosts)
    {
        var result = new List<PeerShare>();
        if (!await HasSambaAsync())
        {
            return result;
        }
        foreach (var host in hosts)
        {
            // -g 输出格式：Disk|名称|备注 / Printer|名称|备注
            var r = await RunAsync(
                $"timeout 8 smbclient -L //{host} -N -g 2>/dev/null");
            foreach (var line in r.Out.Split('\n'))
            {
                var parts = line.Trim().Split('|');
                if (parts.Length < 2)
                {
                    continue;
                }
                var kind = parts[0].Trim();
                var name = parts[1].Trim();
                if (name.EndsWith('$') || (kind != "Disk" && kind != "Printer"))
                {
                    continue;
                }
                result.Add(new PeerShare { Host = host, Name = name, IsPrinter = kind == "Printer" });
            }
        }
        return result;
    }

    /* ---------------- 挂载队友共享 ---------------- */

    public static async Task<List<MountedShare>> ListMountsAsync()
    {
        var list = new List<MountedShare>();
        var r = await RunAsync("mount | grep ' type cifs '");
        foreach (var line in r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');
            if (parts.Length >= 3)
            {
                list.Add(new MountedShare { Unc = parts[0], Point = parts[2] });
            }
        }
        return list;
    }

    public static async Task MountAsync(string unc, string point, string user, string pass)
    {
        var all = await ListMountsAsync();
        if (all.Any(m => m.Point == point))
        {
            throw new InvalidOperationException($"{point} 已经挂载了别的东西");
        }
        await RunAsync($"mkdir -p {Q(point)}");
        var opt = user.Length > 0
            ? $"username={user},password={pass},vers=3.0"
            : "guest,vers=3.0";
        var r = await RunAsync($"mount -t cifs {Q(unc.Replace('\\', '/'))} {Q(point)} -o {opt} 2>&1");
        if (r.Code != 0)
        {
            throw new InvalidOperationException("挂载失败：" + (r.Err.Length > 0 ? r.Err : r.Out));
        }
    }

    public static async Task UnmountAsync(string point)
    {
        var r = await RunAsync($"umount {Q(point)} 2>&1");
        if (r.Code != 0)
        {
            throw new InvalidOperationException("卸载失败：" + (r.Err.Length > 0 ? r.Err : r.Out));
        }
    }

    /* ---------------- 打印机（CUPS） ---------------- */

    public static async Task<(bool Installed, string Printers)> PrinterStatusAsync()
    {
        var has = (await RunAsync("command -v cupsd >/dev/null 2>&1 && echo yes || echo no"))
            .Out.Trim() == "yes";
        if (!has)
        {
            return (false, "");
        }
        var list = await RunAsync("lpstat -p 2>/dev/null | head -10");
        return (true, list.Out.Trim());
    }

    /// <summary>开启 CUPS 网络共享（组网内其他机器可以添加这台机器的打印机）</summary>
    public static async Task<string> EnablePrinterSharingAsync(Action<string>? log = null)
    {
        if ((await PrinterStatusAsync()).Installed == false)
        {
            log?.Invoke("正在安装 CUPS（打印机服务）…");
            var r = await RunAsync(
                "export DEBIAN_FRONTEND=noninteractive; "
                + "apt-get update -qq && apt-get install -y -qq cups 2>&1 | tail -3");
            if ((await PrinterStatusAsync()).Installed == false)
            {
                throw new InvalidOperationException("安装 CUPS 失败，请手动执行：sudo apt install -y cups\n" + r.Out + r.Err);
            }
        }
        await RunAsync("cupsctl --share-printers --remote-admin 2>&1");
        await RunAsync("systemctl enable --now cups 2>/dev/null || service cups start 2>/dev/null || true");
        return "CUPS 共享已开启";
    }
}
