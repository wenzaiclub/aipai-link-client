using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiPai.Compact.Services;
using AppiieNet.Client.Models;

namespace AiPai.Compact.Views;

public partial class HomeView : UserControl, IRefreshable
{
    private string _tab = "my";

    // 展开状态：哪些组网展开了、我的设备区块是否展开（默认都折叠，避免一屏堆满设备）
    private readonly HashSet<int> _expanded = new();
    private bool _myDevicesExpanded;

    public HomeView()
    {
        InitializeComponent();
        Refresh();
    }

    public void Refresh()
    {
        var st = AppState.Current;

        // 开发预览：AIPAI_UI_PREVIEW=expanded 时默认展开所有组网（只用于本地看排版）
        if (Environment.GetEnvironmentVariable("AIPAI_UI_PREVIEW") == "expanded")
        {
            foreach (var n in st.Networks) { _expanded.Add(n.Id); }
        }

        // ---- 状态条 ----
        TxtState.Text = st.ConnStateText;
        Dot.Fill = (Brush)FindRes(st.ConnState switch
        {
            "connected" => "OkBrush",
            "connecting" => "WarnBrush",
            "failed" => "DangerBrush",
            _ => "TextDim",
        });
        TxtNet.Text = st.Selected?.Name ?? "";

        if (st.ConnState == "connected" && st.LinkType.Length > 0)
        {
            BadgeLink.Visibility = Visibility.Visible;
            TxtLink.Text = st.LinkType;
            TxtSpeed.Text = $"↓{st.RxKb:0.#} ↑{st.TxKb:0.#} KB/s";
        }
        else
        {
            BadgeLink.Visibility = Visibility.Collapsed;
            TxtSpeed.Text = "";
        }

        TxtCode.Text = st.Selected == null ? "未选择组网" : "适配码 " + st.Selected.Code;
        TxtRight.Text = st.Selected == null
            ? ""
            : (st.ConnState == "connected"
                ? st.DisplayIp + " · " + st.RoleText
                : st.RoleText + " · " + st.ServerLocation);

        var hint = st.StatusText is "已连接" or "未连接" ? "" : st.StatusText;
        TxtHint.Text = hint;
        TxtHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        BtnConnect.Content = st.ConnState == "connected" ? "断开" : (st.Busy ? "…" : "连接");
        BtnConnect.IsEnabled = !st.Busy;
        BtnLeave.Visibility = st.Selected == null ? Visibility.Collapsed : Visibility.Visible;
        BtnLeave.IsEnabled = !st.Busy;

        // 组网额度：只统计「自己创建的」，加入别人的不占额度
        var owned = st.Networks.Count(n => n.Role == "owner");
        var maxNets = st.Login?.User.Level?.MaxNetworks ?? 0;
        TxtQuota.Text = maxNets > 0
            ? $"我自己创建的：{owned} / {maxNets} 个"
            : $"我自己创建的：{owned} 个";

        BuildLists();
        UpdateTabs();
    }

    private void BuildLists()
    {
        var st = AppState.Current;
        ListMy.Children.Clear();
        ListShared.Children.Clear();

        var mine = st.Networks.Where(n => n.Role == "owner").ToList();
        var joined = st.Networks.Where(n => n.Role != "owner").ToList();

        TabMyText.Text = $"我的网络({st.MyDevices.Count})";
        TabSharedText.Text = $"共享组网({joined.Count})";

        // 缺成员的组，后台补一次（回来后会再 Refresh 一次）
        foreach (var n in st.Networks.Where(n => st.NeedsMembers(n.Id)).ToList())
        {
            _ = st.LoadMembersAsync(n.Id);
        }

        BuildMyPane(st, mine);
        Fill(ListShared, joined, "还没有加入别人的组网，输入适配码即可加入");
    }

    /// <summary>「我的网络」= 本账号的设备 + 我自己创建的组网</summary>
    private void BuildMyPane(AppState st, List<NetworkSummary> mine)
    {
        // 我的设备
        var myId2 = st.Login?.Device?.Id ?? 0;
        var onlineDevices = st.MyDevices.Where(d => st.DeviceOnline(d) == true || d.Id == myId2).ToList();
        var hiddenCount = st.MyDevices.Count - onlineDevices.Count;
        ListMy.Children.Add(SectionHeaderToggle($"我的设备({onlineDevices.Count})", _myDevicesExpanded, () =>
        {
            _myDevicesExpanded = !_myDevicesExpanded;
            Refresh();
        }));

        if (!_myDevicesExpanded)
        {
            // 折叠状态：只留标题
        }
        else if (onlineDevices.Count == 0)
        {
            ListMy.Children.Add(EmptyHint(st.MyDevices.Count == 0
                ? "这个账号下还没有设备"
                : "当前没有在线设备（离线设备不在此处显示）"));
        }
        else
        {
            foreach (var d in onlineDevices)
            {
                var myId = myId2;
                var online = st.DeviceOnline(d);
                var card = new Border
                {
                    Background = (Brush)FindRes("CardBg"),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(11, 8, 11, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    BorderBrush = (Brush)FindRes("LineBrush"),
                    BorderThickness = new Thickness(1),
                };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
                nameLine.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = (Brush)FindRes(online == true ? "OkBrush" : (online == false ? "TextDim" : "TextDim")),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var nm = d.DeviceName.Length > 0 ? d.DeviceName : d.DeviceUid;
                if (d.Id == myId && myId != 0) { nm += "（本机）"; }
                nameLine.Children.Add(new TextBlock
                {
                    Text = nm,
                    FontSize = 13.5,
                    Foreground = (Brush)FindRes("TextPrimary"),
                    Margin = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                left.Children.Add(nameLine);
                left.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(d.Platform) ? "设备" : d.Platform,
                    FontSize = 11.5,
                    Foreground = (Brush)FindRes("TextDim"),
                    Margin = new Thickness(13, 3, 0, 0),
                });
                Grid.SetColumn(left, 0);
                g.Children.Add(left);

                var status = new TextBlock
                {
                    Text = online == true ? "在线" : (online == false ? "离线" : "未知"),
                    FontSize = 12,
                    Foreground = (Brush)FindRes(online == true ? "OkBrush" : "TextDim"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(status, 1);
                g.Children.Add(status);

                card.Child = g;
                ListMy.Children.Add(card);
            }
        }

        // 我自己创建的组网
        ListMy.Children.Add(SectionHeader("我创建的组网"));
        if (mine.Count == 0)
        {
            ListMy.Children.Add(EmptyHint("还没有自己创建的组网，去「创建组网」建一个"));
            return;
        }
        foreach (var n in mine) { ListMy.Children.Add(MakeRow(n)); }
    }

    private TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)FindRes("TextSecond"),
        Margin = new Thickness(2, 6, 0, 7),
    };

    /// <summary>可点击展开/折叠的小标题（前面带 ▸/▾）</summary>
    private Grid SectionHeaderToggle(string text, bool expanded, Action onClick)
    {
        var g = new Grid
        {
            Margin = new Thickness(2, 6, 2, 7),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = expanded ? "▾" : "▸",
            FontSize = 11,
            Width = 12,
            Foreground = (Brush)FindRes("TextDim"),
        });
        sp.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindRes("TextSecond"),
        });
        g.Children.Add(sp);
        g.MouseLeftButtonUp += (_, __) => onClick();
        return g;
    }

    private TextBlock EmptyHint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = (Brush)FindRes("TextDim"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 0, 2, 8),
    };

    private void Fill(Panel host, List<NetworkSummary> list, string empty)
    {
        if (list.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = empty,
                FontSize = 11,
                Foreground = (Brush)FindRes("TextDim"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 2, 0),
            });
            return;
        }
        foreach (var n in list) { host.Children.Add(MakeRow(n)); }
    }

    /// <summary>
    /// 组网卡片：默认折叠，只显示「组网名 / 角色 / 适配码 / 设备数 / 连接按钮」；
    /// 点左半边展开后，才显示组内每台设备的在线状态。
    /// </summary>
    private Button MakeTransferButton(NetworkSummary n)
    {
        var btn = new Button
        {
            Content = "转让",
            Width = 42,
            Height = 22,
            FontSize = 10.5,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindRes("BtnGhost"),
            ToolTip = "把房主转让给组网里的其他账号",
        };
        btn.Click += (_, __) =>
        {
            var st = AppState.Current;
            if (st.MembersOf(n.Id).Count == 0)
            {
                _ = st.LoadMembersAsync(n.Id);
                TxtHint.Text = "成员信息还在加载，稍等一下再点「转让」";
                TxtHint.Visibility = Visibility.Visible;
                return;
            }
            var win = new TransferOwnerWindow(n, st.Login?.Token ?? "")
            {
                Owner = Window.GetWindow(this),
            };
            if (win.ShowDialog() == true)
            {
                TxtHint.Text = "已转让组网「" + n.Name + "」，需要重新选择要连接的组网";
                TxtHint.Visibility = Visibility.Visible;
                Refresh();
            }
        };
        return btn;
    }

    private Border MakeRow(NetworkSummary n)
    {
        var st = AppState.Current;
        var active = st.Selected != null && st.Selected.Id == n.Id;
        var expanded = _expanded.Contains(n.Id);
        var members = st.MembersOf(n.Id);
        var onlineCount = members.Count(m => m.Online);

        var card = new Border
        {
            Background = (Brush)FindRes(active ? "CardHover" : "CardBg"),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 8, 9, 8),
            Margin = new Thickness(0, 0, 0, 6),
            BorderBrush = (Brush)FindRes(active ? "AccentBrush" : "LineBrush"),
            BorderThickness = new Thickness(active ? 1.3 : 1),
        };

        var root = new StackPanel();

        // ---- 头部：▸/▾ + 组网名 + 角色 ……………… [连接] ----
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        headLeft.Children.Add(new TextBlock
        {
            Text = expanded ? "▾" : "▸",
            FontSize = 11,
            Width = 12,
            Foreground = (Brush)FindRes("TextDim"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 组网名前面的状态灯：组里有在线设备就亮绿灯（折叠时也能一眼看出来）
        headLeft.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = (Brush)FindRes(onlineCount > 0 ? "OkBrush" : "TextDim"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = onlineCount > 0 ? $"{onlineCount} 台设备在线" : "当前没有设备在线",
        });

        headLeft.Children.Add(new TextBlock
        {
            Text = n.Name,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindRes(active ? "OkBrush" : "TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        headLeft.Children.Add(new TextBlock
        {
            Text = "· " + n.RoleText,
            FontSize = 12,
            Foreground = (Brush)FindRes("TextSecond"),
            Margin = new Thickness(6, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        headLeft.MouseLeftButtonUp += (_, __) =>
        {
            if (!_expanded.Remove(n.Id)) { _expanded.Add(n.Id); }
            Refresh();
        };

        // 「转让房主」紧跟在“房主”后面，只在自己创建的组网上出现
        var headWrap = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headWrap.Children.Add(headLeft);
        if (n.Role == "owner") { headWrap.Children.Add(MakeTransferButton(n)); }
        Grid.SetColumn(headWrap, 0);
        head.Children.Add(headWrap);

        var headRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 房主可以删除组网（需要临时验证码确认）
        if (n.Role == "owner")
        {
            var btnDelete = new Button
            {
                Content = "删除",
                Width = 46,
                Height = 26,
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindRes("BtnGhost"),
            };
            btnDelete.Click += async (_, __) =>
            {
                var win = new DeleteNetworkWindow(n, AppState.Current.Login?.Token ?? "")
                {
                    Owner = Window.GetWindow(this),
                };
                if (win.ShowDialog() == true)
                {
                    AppState.Current.Selected = null;
                    TxtHint.Text = "已删除组网「" + n.Name + "」";
                    TxtHint.Visibility = Visibility.Visible;
                    await AppState.Current.RefreshNetworksAsync();
                    Refresh();
                }
            };
            headRight.Children.Add(btnDelete);
        }

        var btnConnect = new Button
        {
            Content = active && st.ConnState == "connected" ? "断开" : "连接",
            Width = 56,
            Height = 26,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindRes("BtnPrimary"),
        };
        btnConnect.Click += async (_, __) =>
        {
            var cur = AppState.Current;
            if (cur.Selected?.Id != n.Id)
            {
                cur.Selected = n;
                cur.Raise();
                await cur.RefreshMembersAsync();
            }
            if (cur.ConnState == "connected") { await cur.DisconnectAsync(); }
            else { await cur.ConnectAsync(); }
            Refresh();
        };
        headRight.Children.Add(btnConnect);
        Grid.SetColumn(headRight, 1);
        head.Children.Add(headRight);
        root.Children.Add(head);

        // ---- 副行：适配码 + 复制 + 设备统计 ……………… 本机在该组的 IP ----
        var sub = new Grid { Margin = new Thickness(12, 4, 0, 0) };
        sub.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sub.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sub.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sub.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var code = new TextBlock
        {
            Text = n.Code,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)FindRes("TextSecond"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(code, 0);
        sub.Children.Add(code);

        var copy = new Button
        {
            Content = "复制",
            Height = 23,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0),
            Style = (Style)FindRes("BtnGhost"),
        };
        copy.Click += (_, __) =>
        {
            try
            {
                Clipboard.SetText(n.Code);
                TxtHint.Text = "已复制适配码 " + n.Code;
                TxtHint.Visibility = Visibility.Visible;
            }
            catch { }
        };
        Grid.SetColumn(copy, 1);
        sub.Children.Add(copy);

        // 设备统计占中间剩余宽度，太长就省略，保证右边的本机组网 IP 永远看得见
        var stat = new TextBlock
        {
            Text = members.Count == 0 ? "· 成员信息加载中…" : $"· {members.Count} 台设备（{onlineCount} 在线）",
            FontSize = 12,
            Foreground = (Brush)FindRes("TextDim"),
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ClipToBounds = true,
        };
        Grid.SetColumn(stat, 2);
        sub.Children.Add(stat);

        var ip = new TextBlock
        {
            Text = n.DisplayIpv4,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)FindRes("TextDim"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(ip, 3);
        sub.Children.Add(ip);
        root.Children.Add(sub);

        // ---- 展开区：组内每台设备的在线状态 ----
        if (expanded)
        {
            if (members.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "还没有设备加入这个组网",
                    FontSize = 10.5,
                    Foreground = (Brush)FindRes("TextDim"),
                    Margin = new Thickness(12, 7, 0, 2),
                });
            }
            else
            {
                var box = new StackPanel { Margin = new Thickness(12, 7, 0, 2) };
                var myDeviceId = st.Login?.Device?.Id ?? 0;
                foreach (var m in members.OrderByDescending(x => x.Online))
                {
                    // 用四列固定布局：状态点 / 设备名 / 在线状态 / IP 右对齐，保证每行 IP 都对齐
                    var line = new Grid { Margin = new Thickness(0, 3, 0, 0) };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    line.ColumnDefinitions.Add(new ColumnDefinition());
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

                    var dot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Fill = (Brush)FindRes(m.Online ? "OkBrush" : "TextDim"),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(dot, 0);
                    line.Children.Add(dot);

                    var name = m.DeviceName.Length > 0 ? m.DeviceName : m.Username;
                    if (m.IsPhone) { name += "（手机）"; }
                    if (m.DeviceId == myDeviceId && myDeviceId != 0) { name += "（本机）"; }
                    var nameText = new TextBlock
                    {
                        Text = name,
                        FontSize = 12.5,
                        Foreground = (Brush)FindRes(m.Online ? "TextPrimary" : "TextDim"),
                        Margin = new Thickness(7, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    Grid.SetColumn(nameText, 1);
                    line.Children.Add(nameText);

                    var statusText = new TextBlock
                    {
                        Text = m.Online ? "在线" : "离线",
                        FontSize = 11.5,
                        Foreground = (Brush)FindRes(m.Online ? "OkBrush" : "TextDim"),
                        Margin = new Thickness(6, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Right,
                    };
                    Grid.SetColumn(statusText, 2);
                    line.Children.Add(statusText);

                    if (!string.IsNullOrEmpty(m.V4Addr))
                    {
                        var ipText = new TextBlock
                        {
                            Text = m.V4Addr,
                            FontSize = 11.5,
                            FontFamily = new FontFamily("Consolas"),
                            Foreground = (Brush)FindRes("TextDim"),
                            VerticalAlignment = VerticalAlignment.Center,
                            TextAlignment = TextAlignment.Right,
                        };
                        Grid.SetColumn(ipText, 3);
                        line.Children.Add(ipText);
                    }
                    box.Children.Add(line);
                }
                root.Children.Add(box);
            }
        }

        card.Child = root;
        return card;
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        _tab = ((Button)sender).Tag as string ?? "my";
        PaneMy.Visibility = _tab == "my" ? Visibility.Visible : Visibility.Collapsed;
        PaneShared.Visibility = _tab == "shared" ? Visibility.Visible : Visibility.Collapsed;
        PaneCreate.Visibility = _tab == "create" ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabs();
    }

    private void UpdateTabs()
    {
        var on = (Brush)FindRes("AccentBrush");
        var off = (Brush)FindRes("TextSecond");
        var clear = Brushes.Transparent;
        TabMyText.Foreground = _tab == "my" ? on : off;
        TabSharedText.Foreground = _tab == "shared" ? on : off;
        TabCreateText.Foreground = _tab == "create" ? on : off;
        TabMyLine.Background = _tab == "my" ? on : clear;
        TabSharedLine.Background = _tab == "shared" ? on : clear;
        TabCreateLine.Background = _tab == "create" ? on : clear;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var st = AppState.Current;
        if (st.ConnState == "connected") { await st.DisconnectAsync(); }
        else { await st.ConnectAsync(); }
        Refresh();
    }

    /// <summary>状态条上的刷新按钮：重新拉组网列表（已删的会消失）+ 刷新当前组网状态</summary>
    private async void RefreshState_Click(object sender, RoutedEventArgs e)
    {
        var st = AppState.Current;
        BtnRefreshState.IsEnabled = false;
        BtnRefreshState.Content = "…";
        string msg;
        try
        {
            await st.RefreshStatusAsync();
            msg = st.Networks.Count == 0
                ? "已刷新：当前账号下没有组网"
                : $"已刷新：{st.Networks.Count} 个组网";
        }
        catch (Exception ex)
        {
            msg = "刷新失败：" + ex.Message;
        }
        BtnRefreshState.Content = "⟳";
        BtnRefreshState.IsEnabled = true;
        // Refresh() 会把提示行重新写成连接状态，所以刷新结果要在它之后再写
        Refresh();
        TxtHint.Text = msg;
        TxtHint.Visibility = Visibility.Visible;
    }

    /// <summary>点状态条上的适配码直接复制</summary>
    private void CopyCode_Click(object sender, MouseButtonEventArgs e)
    {
        var code = AppState.Current.Selected?.Code;
        if (string.IsNullOrEmpty(code)) { return; }
        try
        {
            Clipboard.SetText(code);
            TxtHint.Text = "已复制适配码 " + code;
            TxtHint.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        var st = AppState.Current;
        var n = st.Selected;
        if (n == null) { return; }
        var ask = MessageBox.Show(
            $"确定退出组网「{n.Name}」？\n\n退出后会断开连接，之后需要重新输入适配码才能加入。",
            "退出组网", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) { return; }

        BtnLeave.IsEnabled = false;
        try
        {
            await st.LeaveNetworkAsync(n);
            TxtHint.Text = "已退出组网 " + n.Name;
            TxtHint.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            TxtHint.Text = "退出失败：" + ex.Message;
            TxtHint.Visibility = Visibility.Visible;
        }
        finally
        {
            BtnLeave.IsEnabled = true;
            Refresh();
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtNewName.Text.Trim();
        if (name.Length < 2)
        {
            SetMsg("请输入组网名称（至少 2 个字）", true);
            return;
        }
        BtnCreate.IsEnabled = false;
        BtnCreate.Content = "创建中…";
        try
        {
            var cfg = await AppState.Current.CreateNetworkAsync(name);
            var nodeTip = AppState.Current.PickedNodeText;
            SetMsg($"创建成功，适配码 {cfg.Code}（已复制）"
                   + (nodeTip.Length > 0 ? "　" + nodeTip : ""), false);
            try { Clipboard.SetText(cfg.Code); } catch { }
            TxtNewName.Text = "";
        }
        catch (Exception ex) { SetMsg(ex.Message, true); }
        finally
        {
            BtnCreate.IsEnabled = true;
            BtnCreate.Content = "创建组网";
            Refresh();
        }
    }

    /// <summary>共享组网页：输入适配码加入别人的组网</summary>
    private async void JoinShared_Click(object sender, RoutedEventArgs e)
    {
        var code = (TxtSharedCode.Text ?? "").Trim();
        if (code.Length < 4)
        {
            SetSharedMsg("请输入正确的适配码", true);
            return;
        }
        BtnSharedJoin.IsEnabled = false;
        BtnSharedJoin.Content = "加入中…";
        try
        {
            var cfg = await AppState.Current.JoinNetworkAsync(code);
            SetSharedMsg($"已加入「{cfg.Name}」（适配码 {cfg.Code}）", false);
            TxtSharedCode.Text = "";
            AppiieNet.Client.Services.AppLog.Client($"[加入组网] 适配码 {code} → {cfg.Name}");
        }
        catch (Exception ex)
        {
            SetSharedMsg(ex.Message, true);
            AppiieNet.Client.Services.AppLog.Client($"[加入组网] 适配码 {code} 失败：{ex.Message}");
        }
        finally
        {
            BtnSharedJoin.IsEnabled = true;
            BtnSharedJoin.Content = "加入组网";
            Refresh();
        }
    }

    private void SetSharedMsg(string text, bool error)
    {
        TxtSharedMsg.Text = text;
        TxtSharedMsg.Foreground = (Brush)FindRes(error ? "DangerBrush" : "OkBrush");
    }

    /// <summary>输入框为空时显示置灰提示（WPF 没有原生 placeholder）</summary>
    private void SharedCode_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 初始化时 TxtSharedHint 还没建好，事件会先到，这里直接跳过
        if (TxtSharedHint == null) { return; }
        TxtSharedHint.Visibility = string.IsNullOrEmpty(TxtSharedCode.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetMsg(string text, bool error)
    {
        TxtCreateMsg.Text = text;
        TxtCreateMsg.Foreground = (Brush)FindRes(error ? "DangerBrush" : "OkBrush");
    }

    private static object FindRes(string key) => Application.Current.FindResource(key);
}
