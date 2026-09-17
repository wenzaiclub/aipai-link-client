using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiPai.Compact.Services;

namespace AiPai.Compact.Views;

public partial class ToolsView : UserControl, IRefreshable
{
    private record Tile(string Title, string Icon, string Color, Action Action);

    private readonly List<Tile> _tiles;

    public ToolsView()
    {
        InitializeComponent();
        _tiles = new List<Tile>
        {
            new("文件共享",
                "M3.5,6.5 H9 L11,8.5 H20.5 A1,1 0 0 1 21.5,9.5 V18 A1,1 0 0 1 20.5,19 H3.5 A1,1 0 0 1 2.5,18 V7.5 A1,1 0 0 1 3.5,6.5 Z",
                "#FF2F6BFF",
                () => OpenRemoteFiles("share")),
            new("打印机共享",
                "M7,8 V4.5 H17 V8 M4.5,8 H19.5 V15.5 H16 V19.5 H8 V15.5 H4.5 Z",
                "#FF7C5CFF",
                () => OpenRemoteFiles("printer")),
            new("远程桌面",
                "M3.5,5 H20.5 V15.5 H3.5 Z M9,20 H15 M12,15.5 V20",
                "#FF22C55E",
                () => Info("远程桌面", "组网连通后，按 Win+R 输入 mstsc，填对方的组网 IP 即可远程桌面；\n对方需要开启「允许远程桌面」。")),
            new("网络诊断",
                "M2.5,12 H6 L8,6.5 L11,17.5 L13,12 H21.5",
                "#FFF59E0B",
                () => Info("网络诊断", "查看本机组网 IP、NAT 类型、连接方式（P2P/中继）与实时速率。")),
            new("网页控制台",
                "M12,3.5 A8.5,8.5 0 1 0 12,20.5 A8.5,8.5 0 1 0 12,3.5 M3.5,12 H20.5 M12,3.5 C15,6.5 15,17.5 12,20.5 C9,17.5 9,6.5 12,3.5",
                "#FF06B6D4",
                OpenConsole),
            new("查看日志",
                "M6.5,3.5 H13.5 L18,8 V20.5 H6.5 Z M13.5,3.5 V8 H18",
                "#FF94A3B8",
                OpenLogs),
        };
        Build();
    }

    public void Refresh() { }

    private void Build()
    {
        Wall.Items.Clear();
        foreach (var t in _tiles)
        {
            var box = new Border
            {
                Height = 78,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(10),
                Background = (Brush)Application.Current.FindResource("CardBg"),
                BorderBrush = (Brush)Application.Current.FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var dot = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(9),
                Background = (Brush)new BrushConverter().ConvertFromString(t.Color)!,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            dot.Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(t.Icon),
                Stroke = Brushes.White,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = Brushes.Transparent,
                Stretch = Stretch.Uniform,
                Width = 20,
                Height = 20,
            };
            sp.Children.Add(dot);
            sp.Children.Add(new TextBlock
            {
                Text = t.Title,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
            });
            box.Child = sp;
            box.MouseLeftButtonUp += (_, __) => t.Action();
            Wall.Items.Add(box);
        }
    }

    /// <summary>非模态提示（不要用系统 MessageBox，会把面板锁住）</summary>
    private static void Info(string title, string text)
    {
        var win = new InfoWindow(title, text);
        if (Application.Current.MainWindow is { IsVisible: true } owner) { win.Owner = owner; }
        win.Show();
    }

    /// <summary>远程文件：扫描组网内其他设备的共享（文件夹 + 打印机）</summary>
    private static void OpenRemoteFiles(string page)
    {
        // 注意：这里绝不能用模态对话框（MessageBox）——
        // 面板是无边框小窗，模态框会把面板锁住，用户会觉得"卡死、关不掉"。
        // 没连组网也没关系，窗口内部会给提示。
        var win = new Views.RemoteFilesWindow(page);
        if (Application.Current.MainWindow is { IsVisible: true } owner) { win.Owner = owner; }
        try { win.Show(); win.Activate(); }
        catch (Exception ex) { AppiieNet.Client.Services.AppLog.Client("打开远程文件窗口失败：" + ex.Message); }
    }

    private static void OpenConsole() => Shell("https://net.appiie.cn/");

    private static void OpenLogs() => Shell(AppiieNet.Client.Services.AppLog.Dir);

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
}
