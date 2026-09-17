using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AiPai.Compact.Services;
using AppiieNet.Client.Models;
using AppiieNet.Client.Services;

namespace AiPai.Compact.Views;

/// <summary>
/// 删除组网：房主输入临时验证码确认。
/// 验证码优先发到账号邮箱；账号未绑定邮箱时由服务端直接返回、在界面上显示。
/// </summary>
public partial class DeleteNetworkWindow : Window
{
    private readonly NetworkSummary _net;
    private readonly string _token;

    public DeleteNetworkWindow(NetworkSummary net, string token)
    {
        InitializeComponent();
        _net = net;
        _token = token;
        TxtNet.Text = $"组网：{net.Name}（{net.Code}）　房主：{net.RoleText}";
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
            top = owner.Top + (owner.Height - 260) / 2;
        }
        else { left = wa.Right - Width - 60; top = wa.Bottom - 320; }
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

    private void SetMsg(string text, bool error)
    {
        TxtMsg.Text = text;
        TxtMsg.Foreground = (Brush)FindRes(error ? "DangerBrush" : "OkBrush");
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        BtnSend.IsEnabled = false;
        BtnSend.Content = "发送中…";
        SetMsg("", false);
        try
        {
            var api = AppState.Current.Api;
            var data = await api.NetworkDeleteCodeAsync(_net.Id, _token);
            if (!string.IsNullOrEmpty(data.Email))
            {
                TxtCodeInfo.Text = $"已发送至 {data.Email}（10 分钟内有效）";
                SetMsg("请到邮箱查收验证码", false);
                AppLog.Client($"[删除组网] 验证码已发送至 {data.Email}");
            }
            else
            {
                TxtCodeInfo.Text = "账号未绑定邮箱，本次验证码：";
                SetMsg(string.IsNullOrEmpty(data.Notice) ? "请输入上方验证码确认删除" : data.Notice, false);
                TxtCode.Text = data.Code;
                AppLog.Client($"[删除组网] 未绑定邮箱，验证码直接下发（{data.Code}）");
            }
        }
        catch (Exception ex)
        {
            SetMsg(ex.Message, true);
            AppLog.Client("[删除组网] 获取验证码失败：" + ex.Message);
        }
        finally
        {
            BtnSend.IsEnabled = true;
            BtnSend.Content = "发送验证码";
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var code = (TxtCode.Text ?? "").Trim();
        if (code.Length != 6)
        {
            SetMsg("请输入 6 位验证码", true);
            return;
        }
        BtnDelete.IsEnabled = false;
        BtnDelete.Content = "删除中…";
        try
        {
            await AppState.Current.Api.NetworkDeleteAsync(_net.Id, code, _token);
            AppLog.Client($"[删除组网] 已删除「{_net.Name}」（适配码 {_net.Code}）");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SetMsg(ex.Message, true);
            AppLog.Client("[删除组网] 删除失败：" + ex.Message);
        }
        finally
        {
            BtnDelete.IsEnabled = true;
            BtnDelete.Content = "确认删除";
        }
    }

    private static object FindRes(string key) => Application.Current.FindResource(key);
}
