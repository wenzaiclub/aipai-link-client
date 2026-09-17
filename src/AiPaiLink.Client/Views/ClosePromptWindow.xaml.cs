using System.Windows;
using System.Windows.Input;
using AiPai.Compact.Services;

namespace AiPai.Compact.Views;

public enum CloseChoice { Cancel, Minimize, Exit }

/// <summary>
/// 关闭面板时问一句：最小化到托盘还是退出。
/// 用自建窗口而不是系统 MessageBox —— 无边框面板上弹 MessageBox 容易把面板锁住。
/// </summary>
public partial class ClosePromptWindow : Window
{
    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

    public ClosePromptWindow()
    {
        InitializeComponent();
        TxtMsg.Text = AppState.Current.ConnState == "connected"
            ? "组网正在运行。要最小化到托盘继续跑，还是退出程序？"
            : "要最小化到托盘，还是完全退出艾派互联？";
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
            top = owner.Top + 80;
        }
        else { left = wa.Right - Width - 60; top = wa.Bottom - 360; }
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
        if (e.Key == Key.Escape) { Cancel_Click(sender, e); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        DialogResult = false;
        Close();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Minimize;
        DialogResult = true;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Exit;
        DialogResult = true;
        Close();
    }
}
