using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AppiieNet.Client.Services;

namespace AiPai.Compact.Views;

/// <summary>客户端 / 引擎日志查看器（对应主客户端的「日志」窗口）。</summary>
public partial class LogWindow : Window
{
    private string _tab = "client";

    public LogWindow()
    {
        InitializeComponent();
        PositionWindow();
        Load();
        Loaded += (_, __) => Activate();
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

    private void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        _tab = ((Button)sender).Tag as string ?? "client";
        Load();
    }

    private void Load()
    {
        var on = (Brush)FindRes("AccentBrush");
        var off = (Brush)FindRes("TextSecond");
        NavClient.Foreground = _tab == "client" ? on : off;
        NavEngine.Foreground = _tab == "engine" ? on : off;

        var file = AppLog.FileOf(_tab);
        TxtFile.Text = file;
        try
        {
            if (!File.Exists(file))
            {
                TxtLog.Text = "（还没有日志文件）";
                return;
            }
            var lines = AppLog.ReadTail(file, 2000);
            TxtLog.Text = string.Join(Environment.NewLine, lines);
            Scroller.ScrollToEnd();
        }
        catch (Exception ex)
        {
            TxtLog.Text = "读取日志失败：" + ex.Message;
        }
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppLog.Dir) { UseShellExecute = true }); } catch { }
    }

    private static object FindRes(string key) => Application.Current.FindResource(key);
}
