using System.Windows;
using System.Windows.Input;

namespace AiPai.Compact.Views;

/// <summary>
/// 非模态提示窗。紧凑版面板不弹系统 MessageBox ——
/// 模态框会把无边框小面板锁住，看起来就是"卡死、关不掉"。
/// </summary>
public partial class InfoWindow : Window
{
    public InfoWindow(string title, string body)
    {
        InitializeComponent();
        TxtTitle.Text = title;
        TxtBody.Text = body;
        PositionWindow();
        Loaded += (_, __) => Activate();
    }

    private void PositionWindow()
    {
        var wa = SystemParameters.WorkArea;
        double left, top;
        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            left = owner.Left + (owner.Width - Width) / 2;
            top = owner.Top - 12;
            UpdateLayout();
            top = owner.Top - ActualHeight - 12;
            if (top < wa.Top + 8) { top = owner.Top + owner.Height + 12; }
        }
        else
        {
            left = wa.Right - Width - 40;
            top = wa.Bottom - 260;
        }
        Left = Math.Max(wa.Left + 8, Math.Min(left, wa.Right - Width - 8));
        UpdateLayout();
        Top = Math.Max(wa.Top + 8, Math.Min(top, wa.Bottom - ActualHeight - 8));
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
