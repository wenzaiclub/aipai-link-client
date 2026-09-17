using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiPai.Compact.Services;
using AiPai.Compact.Views;
using WinForms = System.Windows.Forms;

namespace AiPai.Compact;

public partial class MainWindow : Window
{
    private LoginView? _login;
    private HomeView? _home;
    private ToolsView? _tools;
    private MenuView? _menu;
    private string _page = "login";
    private bool _wasLoggedIn;
    private bool _reallyExit;
    private WinForms.NotifyIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        PositionBottomRight();
        SetupTray();
        AppState.Current.Changed += OnStateChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Closing += Window_Closing;
    }

    // ---------- 托盘 ----------

    /// <summary>托盘图标：右键菜单能显示面板、连断组网、看日志、退出</summary>
    private void SetupTray()
    {
        try
        {
            _tray = new WinForms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "艾派互联",
                Visible = true,
            };
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("显示面板", null, (_, _) => ShowFromTray());
            menu.Items.Add("连接 / 断开", null, (_, _) => ToggleConnect());
            menu.Items.Add("查看日志", null, (_, _) => OpenLogs());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出程序", null, (_, _) =>
            {
                _reallyExit = true;
                Close();
            });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch
        {
            // 托盘建不起来也不影响主流程
        }
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream != null) { return new System.Drawing.Icon(stream); }
        }
        catch { }
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null) { return System.Drawing.Icon.ExtractAssociatedIcon(exe) ?? System.Drawing.SystemIcons.Application; }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void HideToTray()
    {
        Hide();
        try { _tray?.ShowBalloonTip(1500, "艾派互联", "已最小化到托盘，组网继续运行", WinForms.ToolTipIcon.Info); } catch { }
    }

    private async void ToggleConnect()
    {
        var st = AppState.Current;
        if (!st.LoggedIn) { ShowFromTray(); return; }
        try
        {
            if (st.ConnState == "connected") { await st.DisconnectAsync(); }
            else { await st.ConnectAsync(); }
            Dispatcher.Invoke(() => (_home as IRefreshable)?.Refresh());
        }
        catch { }
    }

    private void OpenLogs()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var win = new Views.LogWindow();
                if (IsVisible) { win.Owner = this; }
                win.Show();
            }
            catch { }
        });
    }

    private void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = wa.Bottom - Height - 16;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var st = AppState.Current;
        var resumed = await st.TryResumeAsync();
        // 记住密码的情况下自动登录（和主线客户端一致的手感）
        var cred = st.Settings.LoadCredentials();
        if (!resumed && cred.Password.Length > 0 && cred.Username.Length > 0)
        {
            try
            {
                await st.LoginAsync(cred.Username, cred.Password, true);
                await st.RefreshNetworksAsync();
                _wasLoggedIn = true;
            }
            catch { }
        }
        else if (resumed) { _wasLoggedIn = true; }
        // 开发预览：AIPAI_UI_PREVIEW=home 时直接进首页（只用于本地调试，不影响正常使用）
        var preview = Environment.GetEnvironmentVariable("AIPAI_UI_PREVIEW");
        ShowPage(preview == "home" ? "home" : (st.LoggedIn ? "home" : "login"));
        if (preview == "remotefiles") { new Views.RemoteFilesWindow().Show(); }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        AppState.Current.Changed -= OnStateChanged;
        try { _tray?.Dispose(); } catch { }
        try { AppState.Current.Engine.Stop(); } catch { }
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 关面板时的行为：设置里关了「退出前询问」就直接缩到托盘继续跑；
    /// 开着就弹自己的小窗问一句（不用系统 MessageBox，免得锁住无边框面板）。
    /// </summary>
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExit) { return; }
        var st = AppState.Current;

        if (!st.Settings.ClosePrompt)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        var dlg = new Views.ClosePromptWindow { Owner = this };
        dlg.ShowDialog();
        switch (dlg.Choice)
        {
            case Views.CloseChoice.Minimize:
                e.Cancel = true;
                HideToTray();
                break;
            case Views.CloseChoice.Exit:
                _reallyExit = true;
                break;
            default:
                e.Cancel = true;
                break;
        }
    }

    private void OnStateChanged()
    {
        Dispatcher.Invoke(() =>
        {
            var logged = AppState.Current.LoggedIn;
            BottomNav.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;
            // 登录成功 → 自动进首页；退出登录 → 回登录页。
            // 用「当前停在哪一页」判断，避免事件时序导致卡在登录页。
            if (logged && _page == "login") { ShowPage("home"); }
            else if (!logged && _page != "login") { ShowPage("login"); }
            _wasLoggedIn = logged;
            (_home as IRefreshable)?.Refresh();
            (_menu as IRefreshable)?.Refresh();
            (_tools as IRefreshable)?.Refresh();
            // 托盘提示跟着连接状态走，鼠标一悬停就知道连没连
            if (_tray != null)
            {
                _tray.Text = AppState.Current.ConnState == "connected"
                    ? "艾派互联 - 已连接"
                    : "艾派互联 - 未连接";
            }
        });
    }

    private void ShowPage(string page)
    {
        _page = page;
        BottomNav.Visibility = AppState.Current.LoggedIn ? Visibility.Visible : Visibility.Collapsed;
        switch (page)
        {
            case "login":
                _login ??= new LoginView();
                PageHost.Content = _login;
                break;
            case "tools":
                _tools ??= new ToolsView();
                PageHost.Content = _tools;
                (_tools as IRefreshable)?.Refresh();
                break;
            case "menu":
                _menu ??= new MenuView();
                PageHost.Content = _menu;
                (_menu as IRefreshable)?.Refresh();
                break;
            default:
                _home ??= new HomeView();
                PageHost.Content = _home;
                (_home as IRefreshable)?.Refresh();
                break;
        }
        UpdateNav(page);
    }

    private void UpdateNav(string page)
    {
        var on = (Brush)FindResource("AccentBrush");
        var off = (Brush)FindResource("TextSecond");
        NavHomeText.Foreground = page == "home" ? on : off;
        NavToolsText.Foreground = page == "tools" ? on : off;
        NavMenuText.Foreground = page == "menu" ? on : off;
        NavHomeIcon.Stroke = page == "home" ? on : off;
        NavToolsIcon.Stroke = page == "tools" ? on : off;
        NavMenuIcon.Stroke = page == "menu" ? on : off;
        NavHomeLine.Background = page == "home" ? on : Brushes.Transparent;
        NavToolsLine.Background = page == "tools" ? on : Brushes.Transparent;
        NavMenuLine.Background = page == "menu" ? on : Brushes.Transparent;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (!AppState.Current.LoggedIn) { return; }
        ShowPage(((Button)sender).Tag as string ?? "home");
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); }
    }

    private void More_Click(object sender, RoutedEventArgs e) => ShowPage("menu");

    /// <summary>最小化：设置里勾了「最小化到托盘」就缩到托盘，否则正常最小化到任务栏</summary>
    private void Min_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Current.Settings.MinimizeToTray) { HideToTray(); }
        else { WindowState = WindowState.Minimized; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public interface IRefreshable
{
    void Refresh();
}
