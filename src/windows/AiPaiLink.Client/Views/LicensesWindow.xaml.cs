using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AiPai.Compact.Views;

/// <summary>
/// 开源许可页：列出随软件分发的第三方组件及其许可证。
/// LGPL 组件（EasyTier / WinDivert）以独立进程或动态库方式调用，未修改源码。
/// </summary>
public partial class LicensesWindow : Window
{
    private record Item(string Name, string Purpose, string License, string Url);

    private static readonly Item[] Items =
    {
        new("EasyTier", "组网引擎（easytier-core.exe）", "LGPL-3.0",
            "https://github.com/EasyTier/EasyTier"),
        new("WinDivert", "流量截获（WinDivert64.sys / Packet.dll）", "LGPL-3.0",
            "https://github.com/basil00/WinDivert"),
        new("Wintun", "虚拟网卡（wintun.dll）", "WireGuard Prebuilt Binaries License",
            "https://www.wintun.net/"),
        new(".NET / WPF", "客户端界面框架", "MIT",
            "https://github.com/dotnet/wpf"),
        new("QRCoder", "二维码生成", "MIT",
            "https://github.com/codebude/QRCoder"),
        new("艾派互联客户端", "自有代码", "保留所有权利", ""),
    };

    public LicensesWindow()
    {
        InitializeComponent();
        foreach (var it in Items) { List.Children.Add(MakeRow(it)); }
        PositionWindow();
        Loaded += (_, __) => Activate();
    }

    private Border MakeRow(Item it)
    {
        var card = new Border
        {
            Background = (Brush)Application.Current.FindResource("CardBg"),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 9, 9, 9),
            Margin = new Thickness(0, 0, 0, 7),
            BorderBrush = (Brush)Application.Current.FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = it.Name,
            FontSize = 12.5,
            Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
        });
        left.Children.Add(new TextBlock
        {
            Text = it.Purpose,
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
        });
        left.Children.Add(new TextBlock
        {
            Text = "许可证：" + it.License,
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("TextSecond"),
        });
        Grid.SetColumn(left, 0);
        g.Children.Add(left);

        if (it.Url.Length > 0)
        {
            var btn = new Button
            {
                Content = "源码/官网",
                Height = 24,
                Padding = new Thickness(9, 0, 9, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.FindResource("BtnGhost"),
            };
            var url = it.Url;
            btn.Click += (_, __) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            };
            Grid.SetColumn(btn, 1);
            g.Children.Add(btn);
        }

        card.Child = g;
        return card;
    }

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
}
