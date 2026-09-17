using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AiPai.Compact.Services;
using AppiieNet.Client.Services;

namespace AiPai.Compact.Views;

/// <summary>
/// 软件内热更新：查版本 → 下载 → 校验 → 退出并替换文件 → 自动重启。
/// 走的是服务端的 client_update 接口，不弹窗、不用重新下载安装包。
/// </summary>
public partial class UpdateWindow : Window
{
    private UpdateInfo? _pending;
    private bool _busy;

    public UpdateWindow(bool autoCheck = true)
    {
        InitializeComponent();
        TxtVersion.Text = "当前版本：v" + UpdateService.CurrentVersion;
        PositionWindow();
        Loaded += async (_, __) =>
        {
            Activate();
            if (autoCheck) { await CheckAsync(); }
        };
    }

    private void PositionWindow()
    {
        var wa = SystemParameters.WorkArea;
        double left, top;
        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            left = owner.Left + (owner.Width - Width) / 2;
            top = owner.Top + 60;
        }
        else { left = wa.Right - Width - 60; top = wa.Bottom - 420; }
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
        if (e.Key == Key.Escape && !_busy) { Close(); }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { return; }
        Close();
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        if (_busy) { return; }
        _busy = true;
        BtnCheck.IsEnabled = false;
        BtnUpdate.Visibility = Visibility.Collapsed;
        TxtNotes.Text = "";
        TxtStatus.Foreground = (Brush)FindResource("TextPrimary");
        TxtStatus.Text = "正在检查更新…";
        try
        {
            var api = AppState.Current.Settings.ApiBaseUrl;
            var info = await UpdateService.CheckAsync(api);
            if (info == null || string.IsNullOrWhiteSpace(info.Url))
            {
                TxtStatus.Text = "检查更新失败，请检查网络后重试。";
                return;
            }
            if (!UpdateService.IsNewer(info.Version, UpdateService.CurrentVersion))
            {
                _pending = null;
                TxtStatus.Foreground = (Brush)FindResource("OkBrush");
                TxtStatus.Text = "已经是最新版本（v" + UpdateService.CurrentVersion + "）。";
                return;
            }

            _pending = info;
            TxtStatus.Foreground = (Brush)FindResource("AccentBrush");
            TxtStatus.Text = $"发现新版本 v{info.Version}（当前 v{UpdateService.CurrentVersion}）"
                             + (info.Size.Length > 0 ? "，安装包 " + info.Size : "");
            TxtNotes.Text = info.Notes;
            BtnUpdate.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "检查更新失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            BtnCheck.IsEnabled = true;
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var info = _pending;
        if (info == null || _busy) { return; }
        _busy = true;
        BtnCheck.IsEnabled = false;
        BtnUpdate.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        Bar.Value = 0;
        try
        {
            var api = AppState.Current.Settings.ApiBaseUrl;
            var progress = new Progress<(int percent, string text)>(p =>
            {
                Bar.Value = p.percent;
                TxtStatus.Foreground = (Brush)FindResource("TextPrimary");
                TxtStatus.Text = p.text;
            });
            AppLog.Client($"[更新] 开始下载 v{info.Version}");
            var script = await UpdateService.DownloadAndPrepareAsync(info, api, progress);
            AppLog.Client("[更新] 下载完成，准备重启替换");
            TxtStatus.Foreground = (Brush)FindResource("OkBrush");
            TxtStatus.Text = "下载完成，正在重启并替换文件…";
            await Task.Delay(700);

            // 先断开组网，避免替换引擎文件时被占用
            try { await AppState.Current.DisconnectAsync(); } catch { }
            UpdateService.RunUpdater(script);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppLog.Client("[更新] 失败：" + ex.Message);
            Bar.Visibility = Visibility.Collapsed;
            TxtStatus.Foreground = (Brush)FindResource("DangerBrush");
            TxtStatus.Text = "更新失败：" + ex.Message + "\n可以到官网下载安装包手动覆盖。";
            BtnCheck.IsEnabled = true;
            BtnUpdate.IsEnabled = true;
            _busy = false;
        }
    }
}
