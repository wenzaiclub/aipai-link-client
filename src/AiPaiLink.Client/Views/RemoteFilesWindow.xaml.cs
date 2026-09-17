using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiPai.Compact.Services;
using AppiieNet.Client.Models;
using AppiieNet.Client.Services;
using Microsoft.Win32;

namespace AiPai.Compact.Views;

/// <summary>
/// 远程文件（布局对齐参考产品）：
///   左栏：共享文件 / 打印机共享 / 文件浏览 / 传输
///   共享文件：选设备（本机=管理本机共享，其他设备=看对方共享）+ 启用共享开关 + 共享账号 + 本机文件夹列表
///   打印机共享：列出本机打印机，一键共享 / 取消共享，队友连接即可装上
///   文件浏览：设备 → 共享 → 目录 → 文件，支持下载 / 上传
/// </summary>
public partial class RemoteFilesWindow : Window
{
    private readonly AppState _st = AppState.Current;
    private sealed record Entry(string Name, string Full, bool IsDir, bool IsPrinter, long Size, DateTime Modified);

    private string _page;
    private bool _busy;
    private bool _loadingShares;

    // 文件浏览状态
    private string _ip = "";
    private string _name = "";
    private string _dir = "";

    private readonly List<string> _transfers = new();

    /// <summary>远程文件相关操作都写进客户端日志，出问题时能直接查</summary>
    private static void Log(string s) => AppLog.Client("[远程文件] " + s);

    public RemoteFilesWindow(string startPage = "share")
    {
        InitializeComponent();
        _page = startPage;
        PositionWindow();
        TxtSideUser.Text = "· " + (_st.LoggedIn ? _st.Username : "未登录");
        SwShare.IsChecked = _st.Settings.SharePasswordless;
        TxtShareUser.Text = _st.Settings.ShareUser;
        TxtSharePass.Text = _st.Settings.SharePassword;
        ApplyNav(_page);
        Loaded += (_, __) =>
        {
            Activate();
            if (_page == "share") { _ = LoadSharesAsync(); }
            else if (_page == "printer") { _ = LoadPrintersAsync(); }
        };
    }

    // ---------- 窗口 ----------

    private void PositionWindow()
    {
        var wa = SystemParameters.WorkArea;
        double left, top;
        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            left = owner.Left - Width - 10;
            top = owner.Top + owner.Height - Height;
            if (left < wa.Left + 8) { left = owner.Left + owner.Width - Width; top = owner.Top - Height - 10; }
        }
        else { left = wa.Right - Width - 24; top = wa.Bottom - Height - 24; }
        Left = Math.Max(wa.Left + 8, Math.Min(left, wa.Right - Width - 8));
        Top = Math.Max(wa.Top + 8, Math.Min(top, wa.Bottom - Height - 8));
    }

    private void Title_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); }
    }

    private void Win_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>共享文件页上的「看别人的共享 →」：切到文件浏览，并说明映射在哪</summary>
    private void GoBrowse_Click(object sender, RoutedEventArgs e)
    {
        ApplyNav("browse");
        BuildBrowseDevices();
        TxtBrowseTip.Text = "选左边一台设备，右边会列出它共享的文件夹；"
                          + "每个文件夹右边的「映射到本地」就是把对方那个共享挂成本机的一个盘（在「此电脑」里能看到）。";
    }

    private void Busy(bool on, string? tip = null)
    {
        _busy = on;
        Bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        BtnUpload.IsEnabled = !on;
        BtnRefresh.IsEnabled = !on;
        if (tip != null) { TxtBrowseTip.Text = tip; }
    }

    // ---------- 左侧导航 ----------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        ApplyNav(((Button)sender).Tag as string ?? "share");
        if (_page == "share") { _ = LoadSharesAsync(); }
        else if (_page == "printer") { _ = LoadPrintersAsync(); }
        else if (_page == "browse" && DeviceList.Children.Count == 0) { BuildBrowseDevices(); }
        else if (_page == "transfer") { RenderTransfers(); }
    }

    /// <summary>切页 + 高亮左侧当前项（构造函数里也要用，所以单独抽出来）</summary>
    private void ApplyNav(string page)
    {
        _page = page;
        PaneShare.Visibility = _page == "share" ? Visibility.Visible : Visibility.Collapsed;
        PanePrinter.Visibility = _page == "printer" ? Visibility.Visible : Visibility.Collapsed;
        PaneBrowse.Visibility = _page == "browse" ? Visibility.Visible : Visibility.Collapsed;
        PaneTransfer.Visibility = _page == "transfer" ? Visibility.Visible : Visibility.Collapsed;
        var on = (Brush)FindRes("TextPrimary");
        var off = (Brush)FindRes("TextSecond");
        NavShareText.Foreground = _page == "share" ? on : off;
        NavPrinterText.Foreground = _page == "printer" ? on : off;
        NavBrowseText.Foreground = _page == "browse" ? on : off;
        NavTransferText.Foreground = _page == "transfer" ? on : off;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_page == "share") { _ = LoadSharesAsync(); }
        else if (_page == "printer") { _ = LoadPrintersAsync(); }
        else if (_page == "browse") { BuildBrowseDevices(); }
        else { RenderTransfers(); }
    }

    // ---------- 共享文件页 ----------

    /// <summary>共享文件页只管本机共享；要看别人的共享去「文件浏览」</summary>
    private async Task LoadSharesAsync()
    {
        if (_loadingShares || !IsLoaded) { return; }
        _loadingShares = true;
        try
        {
            ShareList.Children.Clear();
            BtnAddFolder.Visibility = Visibility.Visible;
            TxtShareTitle.Text = "本机文件夹 / 打印机";

            var shares = await ShareService.ListSharesAsync();
            if (shares.Count == 0)
            {
                TxtShareTip.Text = "还没有共享。点「+ 添加文件夹」共享目录，或到「打印机共享」里把打印机放出来。";
                return;
            }
            foreach (var s in shares) { ShareList.Children.Add(MakeLocalShareRow(s)); }
            TxtShareTip.Text = $"已共享 {shares.Count} 项。队友用同一个适配码加入后，在「文件浏览」里就能看到。";
        }
        catch (Exception ex) { TxtShareTip.Text = "读取失败：" + ex.Message; }
        finally { _loadingShares = false; }
    }

    private Border MakeLocalShareRow(ShareEntry s)
    {
        var card = RowCard();
        var g = (Grid)card.Child!;
        var left = (StackPanel)g.Children[0];
        left.Children.Add(new TextBlock
        {
            Text = (s.Kind == "printer" ? "🖨 " : "📁 ") + s.Name,
            FontSize = 14, Foreground = (Brush)FindRes("TextPrimary"),
        });
        left.Children.Add(new TextBlock
        {
            Text = s.Target + (s.Unc.Length > 0 ? "   →   " + s.Unc : ""),
            FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)FindRes("TextDim"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var right = (StackPanel)g.Children[1];
        var del = new Button
        {
            Content = "取消共享", Width = 88, Height = 26, FontSize = 12,
            Style = (Style)FindRes("BtnGhost"),
        };
        del.Click += async (_, __) =>
        {
            try
            {
                var r = await ShareService.RemoveShareAsync(s);
                Log("取消共享 " + s.Name + " → " + r);
                AddTransfer($"取消共享 {s.Name}");
                TxtShareTip.Text = r;
                await LoadSharesAsync();
            }
            catch (Exception ex) { Log("取消共享失败：" + ex.Message); TxtShareTip.Text = "取消失败：" + ex.Message; }
        };
        right.Children.Add(del);
        return card;
    }

    private Border RowCard()
    {
        var card = new Border
        {
            Background = (Brush)FindRes("CardBg"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 9, 9, 9),
            Margin = new Thickness(0, 0, 0, 6),
            BorderBrush = (Brush)FindRes("LineBrush"),
            BorderThickness = new Thickness(1),
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(left, 0); g.Children.Add(left);
        Grid.SetColumn(right, 1); g.Children.Add(right);
        card.Child = g;
        return card;
    }

    // ---------- 打印机共享页 ----------

    private void RefreshPrinters_Click(object sender, RoutedEventArgs e) => _ = LoadPrintersAsync();

    private async Task LoadPrintersAsync()
    {
        PrinterList.Children.Clear();
        TxtPrinterTip.Text = "正在读取本机打印机…";
        try
        {
            var printers = await ShareService.ListPrintersAsync();
            var shares = await ShareService.ListSharesAsync();
            var shared = shares.Where(s => s.Kind == "printer").ToList();

            if (printers.Count == 0)
            {
                TxtPrinterTip.Text = "这台电脑上没有找到打印机。先在系统里添加打印机，再回来共享。";
                return;
            }

            foreach (var p in printers)
            {
                var hit = shared.FirstOrDefault(s => string.Equals(s.Target, p.Key, StringComparison.OrdinalIgnoreCase));
                PrinterList.Children.Add(MakePrinterRow(p.Key, p.Display, hit));
            }

            TxtPrinterTip.Text = shared.Count == 0
                ? $"本机 {printers.Count} 台打印机，都还没共享。点右侧「共享」就能给队友用。"
                : $"本机 {printers.Count} 台打印机，已共享 {shared.Count} 台。队友在「共享文件」里选中你的设备，点「连接」即可装上。";
        }
        catch (Exception ex)
        {
            Log("读取打印机失败：" + ex.Message);
            TxtPrinterTip.Text = "读取打印机失败：" + ex.Message;
        }
    }

    private Border MakePrinterRow(string printerName, string display, ShareEntry? shared)
    {
        var card = RowCard();
        var g = (Grid)card.Child!;
        var left = (StackPanel)g.Children[0];

        left.Children.Add(new TextBlock
        {
            Text = "🖨 " + printerName,
            FontSize = 14,
            Foreground = (Brush)FindRes("TextPrimary"),
        });
        left.Children.Add(new TextBlock
        {
            Text = shared == null
                ? display
                : $"共享名 {shared.Name}　→　\\\\本机组网IP\\{shared.Name}",
            FontSize = 11.5,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)FindRes(shared == null ? "TextDim" : "OkBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var right = (StackPanel)g.Children[1];
        if (shared == null)
        {
            var btn = new Button
            {
                Content = "共享",
                Width = 72,
                Height = 26,
                FontSize = 12,
                Style = (Style)FindRes("BtnPrimary"),
            };
            btn.Click += async (_, __) =>
            {
                var shareName = ShareService.SuggestShareName(printerName);
                if (shareName.Length == 0) { shareName = "AiPaiPrinter"; }
                try
                {
                    btn.IsEnabled = false;
                    btn.Content = "共享中…";
                    var r = await ShareService.SharePrinterAsync(printerName, shareName);
                    Log($"共享打印机 {printerName}（共享名 {shareName}）→ {r}");
                    AddTransfer($"共享打印机 {printerName}");
                    TxtPrinterTip.Text = $"已共享「{printerName}」，共享名 {shareName}。队友扫到后点「连接」就能用。";
                    await LoadPrintersAsync();
                }
                catch (Exception ex)
                {
                    Log("共享打印机失败：" + ex.Message);
                    TxtPrinterTip.Text = "共享失败：" + ex.Message;
                    btn.IsEnabled = true;
                    btn.Content = "共享";
                }
            };
            right.Children.Add(btn);
        }
        else
        {
            var copy = new Button
            {
                Content = "复制路径",
                Width = 88,
                Height = 26,
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindRes("BtnGhost"),
            };
            copy.Click += (_, __) =>
            {
                try
                {
                    Clipboard.SetText($"\\\\{_st.DisplayIp}\\{shared.Name}");
                    TxtPrinterTip.Text = $"已复制 \\\\{_st.DisplayIp}\\{shared.Name}，发给队友粘贴到资源管理器即可。";
                }
                catch { }
            };
            right.Children.Add(copy);

            var del = new Button
            {
                Content = "取消共享",
                Width = 88,
                Height = 26,
                FontSize = 12,
                Style = (Style)FindRes("BtnGhost"),
            };
            del.Click += async (_, __) =>
            {
                try
                {
                    del.IsEnabled = false;
                    var r = await ShareService.RemoveShareAsync(shared);
                    Log($"取消共享打印机 {printerName} → {r}");
                    AddTransfer($"取消共享打印机 {printerName}");
                    TxtPrinterTip.Text = r;
                    await LoadPrintersAsync();
                }
                catch (Exception ex)
                {
                    Log("取消共享打印机失败：" + ex.Message);
                    TxtPrinterTip.Text = "取消失败：" + ex.Message;
                    del.IsEnabled = true;
                }
            };
            right.Children.Add(del);
        }
        return card;
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要共享给组网队友的文件夹" };
        if (dlg.ShowDialog() != true) { return; }
        try
        {
            var name = ShareService.SuggestShareName(dlg.FolderName);
            var r = await ShareService.ShareFolderAsync(dlg.FolderName, name);
            Log($"添加共享 {name} ← {dlg.FolderName} → {r}");
            AddTransfer($"添加共享 {name}");
            TxtShareTip.Text = r;
            await LoadSharesAsync();
        }
        catch (Exception ex) { Log("添加共享失败：" + ex.Message); TxtShareTip.Text = "添加失败：" + ex.Message; }
    }

    private async void Share_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SwShare.IsChecked == true)
            {
                var u = (TxtShareUser.Text ?? "").Trim();
                var p = (TxtSharePass.Text ?? "").Trim();
                if (u.Length > 0) { _st.Settings.ShareUser = u; }
                if (p.Length > 0) { _st.Settings.SharePassword = p; }
                _st.Settings.SharePasswordless = true;
                _st.Settings.Save();
                var r = await ShareService.EnablePasswordlessSharingAsync(_st.Settings.ShareUser, _st.Settings.SharePassword);
                Log("启用共享 → " + r);
                AddTransfer("启用共享（免密）");
                TxtShareTip.Text = r + "  队友现在不用输密码就能访问你的共享。";
            }
            else
            {
                _st.Settings.SharePasswordless = false;
                _st.Settings.Save();
                var r = await ShareService.DisablePasswordlessSharingAsync();
                Log("关闭共享 → " + r);
                AddTransfer("关闭共享（免密）");
                TxtShareTip.Text = r;
            }
        }
        catch (Exception ex) { Log("共享开关失败：" + ex.Message); TxtShareTip.Text = "设置失败：" + ex.Message; }
    }

    private void CopyShareUser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{TxtShareUser.Text.Trim()} / {TxtSharePass.Text.Trim()}");
            TxtShareTip.Text = "已复制共享账号，发给队友即可（或让对方勾选「启用共享」自动使用）。";
        }
        catch { }
    }

    /// <summary>
    /// 一键设置共享账号密码：把界面上填的账号密码存下来，并在本机创建/更新这个账号，
    /// 同时放开来宾访问和共享相关的防火墙规则。队友拿到账号密码就能直接连。
    /// </summary>
    private async void SetupShareAccount_Click(object sender, RoutedEventArgs e)
    {
        var user = (TxtShareUser.Text ?? "").Trim();
        var pass = (TxtSharePass.Text ?? "").Trim();
        if (user.Length == 0 || pass.Length == 0)
        {
            TxtShareTip.Text = "账号和密码都要填，填好再点「一键设置」。";
            return;
        }
        if (user.Contains(' ') || user.Contains('"'))
        {
            TxtShareTip.Text = "账号名不要带空格或引号。";
            return;
        }

        try
        {
            _st.Settings.ShareUser = user;
            _st.Settings.SharePassword = pass;
            _st.Settings.SharePasswordless = true;
            _st.Settings.Save();
            SwShare.IsChecked = true;

            TxtShareTip.Text = "正在本机设置共享账号…";
            var r = await ShareService.EnablePasswordlessSharingAsync(user, pass);
            Log($"一键设置共享账号 {user} → {r}");
            AddTransfer($"设置共享账号 {user}");
            TxtShareTip.Text = $"{r}。账号 {user} / {pass}，队友用这个账号密码就能访问你的共享。";
        }
        catch (Exception ex)
        {
            Log("设置共享账号失败：" + ex.Message);
            TxtShareTip.Text = "设置失败：" + ex.Message;
        }
    }

    // ---------- 传输页 ----------

    private void AddTransfer(string text)
    {
        _transfers.Insert(0, DateTime.Now.ToString("MM-dd HH:mm") + "  " + text);
        if (_transfers.Count > 200) { _transfers.RemoveRange(200, _transfers.Count - 200); }
        if (_page == "transfer") { RenderTransfers(); }
    }

    private void RenderTransfers()
    {
        TransferList.Children.Clear();
        if (_transfers.Count == 0)
        {
            TransferList.Children.Add(new TextBlock
            {
                Text = "还没有传输记录。\n文件传输走的是 Windows 共享通道：在「文件浏览」里下载/上传，或直接在资源管理器里拖拽。",
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindRes("TextDim"),
            });
            return;
        }
        foreach (var t in _transfers)
        {
            TransferList.Children.Add(new TextBlock
            {
                Text = t, FontSize = 13, Margin = new Thickness(0, 0, 0, 6),
                Foreground = (Brush)FindRes("TextSecond"),
            });
        }
    }

    // ---------- 文件浏览页 ----------

    private void BuildBrowseDevices()
    {
        DeviceList.Children.Clear();
        FileList.Children.Clear();
        _dir = ""; _ip = ""; _name = "";
        BtnUp.IsEnabled = false;
        TxtPath.Text = "";

        var net = _st.Selected;
        if (net == null)
        {
            TxtBrowseTip.Text = "请先在面板首页选择组网并连接";
            return;
        }
        var myId = _st.Login?.Device?.Id ?? 0;
        var list = _st.MembersOf(net.Id)
            .Where(m => m.DeviceId != myId && !string.IsNullOrWhiteSpace(m.V4Addr))
            .OrderByDescending(m => m.Online)
            .ToList();
        if (list.Count == 0)
        {
            TxtBrowseTip.Text = "这个组网里还没有其他设备。让队友用同一个适配码加入后即可在这里看到。";
            return;
        }
        TxtBrowseTip.Text = $"{list.Count} 台设备，点左侧设备查看它共享的文件夹";
        foreach (var m in list)
        {
            var nm = m.DeviceName.Length > 0 ? m.DeviceName : m.Username;
            if (m.IsPhone) { nm += "（手机）"; }
            var btn = new Button
            {
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = nm, FontSize = 13, Foreground = (Brush)FindRes("TextPrimary") },
                        new TextBlock
                        {
                            Text = (m.Online ? "● " : "○ ") + (m.V4Addr ?? ""), FontSize = 11.5,
                            Margin = new Thickness(0, 2, 0, 0), FontFamily = new FontFamily("Consolas"),
                            Foreground = (Brush)FindRes(m.Online ? "OkBrush" : "TextDim"),
                        },
                    },
                },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(8, 6, 8, 6),
                Cursor = Cursors.Hand,
                Style = (Style)FindRes("BtnGhost"),
            };
            var captured = m;
            btn.Click += async (_, __) => await OpenDeviceAsync(captured);
            DeviceList.Children.Add(btn);
        }
    }

    private async Task OpenDeviceAsync(NetworkMember m)
    {
        if (_busy) { return; }
        _ip = m.V4Addr ?? "";
        _name = m.DeviceName.Length > 0 ? m.DeviceName : m.Username;
        _dir = "";
        FileList.Children.Clear();
        BtnUp.IsEnabled = false;
        TxtPath.Text = $@"\\{_ip}";
        Busy(true, $"正在扫描 {_name}（{_ip}）的共享…");
        var user = _st.Settings.SharePasswordless ? _st.Settings.ShareUser : "";
        var pass = _st.Settings.SharePasswordless ? _st.Settings.SharePassword : "";
        try
        {
            var shares = await ShareService.ScanPeerAsync(_ip, _name, user, pass);
            var entries = shares
                .Select(s => new Entry(s.Name, s.Unc, s.ShareType != 1, s.ShareType == 1, 0, DateTime.MinValue))
                .ToList();
            if (entries.Count == 0)
            {
                TxtBrowseTip.Text = $"{_name} 没有扫描到共享。"
                    + "对方需要先开共享：在那台机器上打开「工具 → 文件共享 → 启用共享」，再「+ 添加文件夹」；"
                    + "也可以检查 445 端口是否被挡、共享账号密码是否正确。";
                return;
            }
            RenderEntries(entries);
            TxtBrowseTip.Text = $"{_name}：{shares.Count} 个共享。点文件夹可以进去，"
                              + "每个共享右边的「映射到本地」能把对方这个文件夹挂成本机的一个盘。";
        }
        catch (Exception ex) { TxtBrowseTip.Text = "扫描失败：" + ex.Message; }
        finally { Busy(false); }
    }

    private async Task LoadDirAsync(string unc)
    {
        if (_busy) { return; }
        Busy(true, "正在读取 " + unc + " …");
        try
        {
            var list = await Task.Run(() =>
            {
                var res = new List<Entry>();
                var di = new DirectoryInfo(unc);
                foreach (var d in di.EnumerateDirectories())
                {
                    try { res.Add(new Entry(d.Name, d.FullName, true, false, 0, d.LastWriteTime)); } catch { }
                }
                foreach (var f in di.EnumerateFiles())
                {
                    try { res.Add(new Entry(f.Name, f.FullName, false, false, f.Length, f.LastWriteTime)); } catch { }
                }
                res.Sort((a, b) => a.IsDir != b.IsDir
                    ? (a.IsDir ? -1 : 1)
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return res;
            });
            _dir = unc;
            BtnUp.IsEnabled = true;
            TxtPath.Text = unc;
            RenderEntries(list);
            TxtBrowseTip.Text = $"{unc}　共 {list.Count} 项" + (list.Count == 0 ? "（空目录）" : "");
        }
        catch (Exception ex) { TxtBrowseTip.Text = "读取失败：" + ex.Message; }
        finally { Busy(false); }
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_dir.Length == 0) { return; }
        var trimmed = _dir.TrimEnd('\\');
        var root = @"\\" + _ip;
        var idx = trimmed.LastIndexOf('\\');
        if (idx < 0 || trimmed.Length <= root.Length)
        {
            var m = _st.MembersOf(_st.Selected?.Id ?? 0).FirstOrDefault(x => (x.V4Addr ?? "") == _ip);
            if (m != null) { _ = OpenDeviceAsync(m); }
            return;
        }
        var parent = trimmed.Substring(0, idx);
        if (parent.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            var m = _st.MembersOf(_st.Selected?.Id ?? 0).FirstOrDefault(x => (x.V4Addr ?? "") == _ip);
            if (m != null) { _ = OpenDeviceAsync(m); }
            return;
        }
        _ = LoadDirAsync(parent);
    }

    private void RenderEntries(List<Entry> list)
    {
        FileList.Children.Clear();
        foreach (var it in list)
        {
            var card = RowCard();
            var g = (Grid)card.Child!;
            var left = (StackPanel)g.Children[0];
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock
            {
                Text = it.IsPrinter ? "🖨" : (it.IsDir ? "📁" : "📄"),
                FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
            });
            line.Children.Add(new TextBlock
            {
                Text = it.Name, FontSize = 14, Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindRes("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 340,
            });
            left.Children.Add(line);

            var right = (StackPanel)g.Children[1];
            if (!it.IsDir && !it.IsPrinter && it.Modified != DateTime.MinValue)
            {
                right.Children.Add(new TextBlock
                {
                    Text = SizeText(it.Size), FontSize = 11.5, Margin = new Thickness(0, 0, 10, 0),
                    Foreground = (Brush)FindRes("TextDim"), VerticalAlignment = VerticalAlignment.Center,
                });
                right.Children.Add(new TextBlock
                {
                    Text = it.Modified.ToString("yyyy-MM-dd HH:mm"), FontSize = 11.5,
                    Margin = new Thickness(0, 0, 10, 0),
                    Foreground = (Brush)FindRes("TextDim"), VerticalAlignment = VerticalAlignment.Center,
                });
            }

            if (it.IsPrinter)
            {
                var connect = new Button
                {
                    Content = "连接", Width = 72, Height = 26, FontSize = 12,
                    Style = (Style)FindRes("BtnPrimary"),
                };
                connect.Click += async (_, __) =>
                {
                    try { Busy(true, "正在连接打印机…"); TxtBrowseTip.Text = await ShareService.ConnectPrinterAsync(it.Full); }
                    catch (Exception ex) { TxtBrowseTip.Text = "连接失败：" + ex.Message; }
                    finally { Busy(false); }
                };
                right.Children.Add(connect);
            }
            else if (!it.IsDir)
            {
                var dl = new Button
                {
                    Content = "下载", Width = 72, Height = 26, FontSize = 12,
                    Style = (Style)FindRes("BtnGhost"),
                };
                var captured = it;
                dl.Click += async (_, __) => await DownloadAsync(captured);
                right.Children.Add(dl);
            }
            else if (_dir.Length == 0)
            {
                // 这一层列出来的就是对方共享的文件夹（根目录），给个一键映射成本地盘
                var map = new Button
                {
                    Content = "映射到本地", Width = 104, Height = 26, FontSize = 12,
                    Style = (Style)FindRes("BtnPrimary"),
                    ToolTip = "把 \\\\" + _ip + "\\" + it.Name + " 挂成一个本地盘符，在「此电脑」里就能用",
                };
                var captured = it;
                map.Click += async (_, __) => await MapShareAsync(captured, map);
                right.Children.Add(map);
            }

            if (it.IsDir && !it.IsPrinter)
            {
                var target = it.Full;
                card.MouseLeftButtonUp += (_, __) => { _ = LoadDirAsync(target); };
            }
            else if (!it.IsPrinter)
            {
                var captured = it;
                card.MouseLeftButtonUp += (_, __) => { _ = DownloadAsync(captured); };
            }
            FileList.Children.Add(card);
        }
    }

    /// <summary>把对方共享的文件夹一键挂成本地盘符（挂完在「此电脑」里出现）</summary>
    private async Task MapShareAsync(Entry share, Button btn)
    {
        if (_busy) { return; }
        var unc = share.Full;
        var letter = ShareService.FreeDriveLetters().FirstOrDefault() ?? "Z:";
        var user = _st.Settings.SharePasswordless ? _st.Settings.ShareUser : "";
        var pass = _st.Settings.SharePasswordless ? _st.Settings.SharePassword : "";

        btn.IsEnabled = false;
        btn.Content = "映射中…";
        Busy(true, $"正在把 {unc} 映射为 {letter} …");
        try
        {
            var r = await ShareService.MapWithFallbackAsync(unc, letter, user, pass);
            ShareService.NotifyDriveChanged(letter, true);
            await ShareService.RefreshExplorerAsync();
            Log($"映射磁盘 {unc} → {letter}（{r}）");
            AddTransfer($"映射 {unc} → {letter}");
            TxtBrowseTip.Text = $"已映射：{letter} → {unc}，打开「此电脑」就能看到这个盘。"
                              + "想断开可以在资源管理器里右键该盘选「断开连接」。";
            btn.Content = "已映射";
        }
        catch (Exception ex)
        {
            Log($"映射 {unc} 失败：{ex.Message}");
            TxtBrowseTip.Text = "映射失败：" + ex.Message
                + "（要对方开着共享、445 能通；也可以进去后右键用「映射网络驱动器」。）";
            btn.IsEnabled = true;
            btn.Content = "映射到本地";
        }
        finally { Busy(false); }
    }

    private async Task DownloadAsync(Entry file)
    {
        if (_busy) { return; }
        var dlg = new SaveFileDialog { FileName = file.Name, Title = "下载到本机" };
        if (dlg.ShowDialog() != true) { return; }
        Busy(true, $"正在下载 {file.Name} …");
        try
        {
            var src = file.Full; var dst = dlg.FileName;
            await Task.Run(() => File.Copy(src, dst, true));
            Log($"下载 {file.Full} → {dst}");
            AddTransfer($"下载 {file.Name}");
            TxtBrowseTip.Text = "已下载到 " + dst;
        }
        catch (Exception ex) { Log("下载失败 " + file.Full + "：" + ex.Message); TxtBrowseTip.Text = "下载失败：" + ex.Message; }
        finally { Busy(false); }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { return; }
        if (_dir.Length == 0) { TxtBrowseTip.Text = "先进入一个文件夹，再上传。"; return; }
        var dlg = new OpenFileDialog { Title = "选择要上传到该目录的文件", Multiselect = true };
        if (dlg.ShowDialog() != true) { return; }
        Busy(true, $"正在上传 {dlg.FileNames.Length} 个文件…");
        try
        {
            var target = _dir;
            var files = dlg.FileNames;
            var n = await Task.Run(() =>
            {
                var ok = 0;
                foreach (var f in files)
                {
                    try { File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true); ok++; } catch { }
                }
                return ok;
            });
            AddTransfer($"上传 {n} 个文件 → {target}");
            Log($"上传 {n}/{files.Length} 个文件 → {target}");
            TxtBrowseTip.Text = $"上传完成：{n}/{files.Length} 个文件";
            await LoadDirAsync(target);
        }
        catch (Exception ex) { Log("上传失败：" + ex.Message); TxtBrowseTip.Text = "上传失败：" + ex.Message; }
        finally { Busy(false); }
    }

    private static string SizeText(long bytes)
    {
        if (bytes >= 1073741824) { return (bytes / 1073741824.0).ToString("0.##") + " GB"; }
        if (bytes >= 1048576) { return (bytes / 1048576.0).ToString("0.#") + " MB"; }
        if (bytes >= 1024) { return (bytes / 1024.0).ToString("0.#") + " KB"; }
        return bytes + " B";
    }

    private static object FindRes(string key) => Application.Current.FindResource(key);
}
