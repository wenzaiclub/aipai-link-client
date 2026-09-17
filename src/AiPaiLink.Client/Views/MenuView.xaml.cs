using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AiPai.Compact.Services;
using AppiieNet.Client.Services;

namespace AiPai.Compact.Views;

public partial class MenuView : UserControl, IRefreshable
{
    public MenuView()
    {
        InitializeComponent();
        Refresh();
    }

    public void Refresh()
    {
        var st = AppState.Current;
        TxtUser.Text = st.LoggedIn ? st.Username : "未登录";
        TxtPlan.Text = st.PlanName;
        TxtDevices.Text = st.DeviceCountText;
        TxtDeviceId.Text = st.ShortDeviceId;
        SwAutoStart.IsChecked = st.AutoStartEnabled;
        SwRelay.IsChecked = st.Settings.BroadcastRelay;
        SwTray.IsChecked = st.Settings.MinimizeToTray;
        SwClosePrompt.IsChecked = st.Settings.ClosePrompt;
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        AppState.Current.Settings.MinimizeToTray = SwTray.IsChecked == true;
        AppState.Current.Settings.Save();
        Refresh();
    }

    private void ClosePrompt_Click(object sender, RoutedEventArgs e)
    {
        AppState.Current.Settings.ClosePrompt = SwClosePrompt.IsChecked == true;
        AppState.Current.Settings.Save();
        Refresh();
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        AppState.Current.AutoStartEnabled = SwAutoStart.IsChecked == true;
        Refresh();
    }

    private void Relay_Click(object sender, RoutedEventArgs e)
    {
        // 局域网广播中继：重连后生效
        AppState.Current.Settings.BroadcastRelay = SwRelay.IsChecked == true;
        AppState.Current.Settings.Save();
        AppState.Current.Raise();
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(AppState.Current.Settings.DeviceUid ?? ""); } catch { }
    }

    private void Alias_Click(object sender, MouseButtonEventArgs e) =>
        Info("别名访问",
            "给组网内的设备起一个固定别名（例如 nas.team），连接后直接用别名打开对应服务。\n\n" +
            "这个功能需要服务端支持，下一版开放。");

    private void Console_Click(object sender, MouseButtonEventArgs e) => Shell("https://net.appiie.cn/");

    /// <summary>软件内热更新：查版本 → 下载 → 重启替换</summary>
    private void Update_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var win = new UpdateWindow();
            if (Application.Current.MainWindow is { IsVisible: true } owner) { win.Owner = owner; }
            win.Show();
        }
        catch (Exception ex)
        {
            // 窗口加载失败也不能把整个面板带崩
            AppLog.Client("[更新] 打开更新窗口失败：" + ex.Message);
            Info("检查更新", "打开更新窗口失败：" + ex.Message + "\n可以到官网下载安装包手动更新。");
        }
    }

    private void Logs_Click(object sender, MouseButtonEventArgs e)
    {
        var win = new LogWindow();
        if (Application.Current.MainWindow is { IsVisible: true } owner) { win.Owner = owner; }
        win.Show();
    }

    private void About_Click(object sender, MouseButtonEventArgs e) =>
        Info("关于", "艾派互联 正式版 v1.0.10\n\n异地组网 · 远程互联\nhttps://net.appiie.cn/");

    private void Licenses_Click(object sender, MouseButtonEventArgs e)
    {
        var win = new LicensesWindow();
        if (Application.Current.MainWindow is { IsVisible: true } owner) { win.Owner = owner; }
        win.Show();
    }

    /// <summary>非模态提示（避免模态框锁住无边框面板）</summary>
    private static void Info(string title, string text)
    {
        var win = new InfoWindow(title, text);
        if (Application.Current.MainWindow is { IsVisible: true } owner) { win.Owner = owner; }
        win.Show();
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定退出登录？退出后会断开组网。", "退出登录",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        AppState.Current.Logout();
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
}
