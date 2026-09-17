using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiPai.Compact.Services;

namespace AiPai.Compact.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        var s = AppState.Current.Settings;
        var cred = s.LoadCredentials();
        TxtUser.Text = cred.Username.Length > 0 ? cred.Username : s.Username;
        if (!string.IsNullOrEmpty(cred.Password)) { TxtPwd.Password = cred.Password; }
    }

    private void GoRegister_Click(object sender, MouseButtonEventArgs e)
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        RegisterPanel.Visibility = Visibility.Visible;
    }

    private void GoLogin_Click(object sender, MouseButtonEventArgs e)
    {
        RegisterPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var user = TxtUser.Text.Trim();
        var pwd = TxtPwd.Password;
        if (user.Length == 0 || pwd.Length == 0)
        {
            SetMsg(TxtLoginMsg, "请输入账号和密码", true);
            return;
        }

        BtnLogin.IsEnabled = false;
        BtnLogin.Content = "登录中…";
        SetMsg(TxtLoginMsg, "", false);
        try
        {
            await AppState.Current.LoginAsync(user, pwd, ChkRemember.IsChecked == true);
            await AppState.Current.RefreshNetworksAsync();
        }
        catch (Exception ex)
        {
            SetMsg(TxtLoginMsg, ex.Message, true);
        }
        finally
        {
            BtnLogin.IsEnabled = true;
            BtnLogin.Content = "登 录";
        }
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        var user = RegUser.Text.Trim();
        var pwd = RegPwd.Password;
        if (user.Length < 3 || pwd.Length < 6)
        {
            SetMsg(TxtRegMsg, "账号至少 3 位，密码至少 6 位", true);
            return;
        }

        BtnRegister.IsEnabled = false;
        BtnRegister.Content = "注册中…";
        SetMsg(TxtRegMsg, "", false);
        try
        {
            var err = await AppState.Current.RegisterAsync(user, pwd, RegEmail.Text.Trim(), RegInvite.Text.Trim());
            if (err != null) { SetMsg(TxtRegMsg, err, true); }
            else
            {
                SetMsg(TxtRegMsg, "注册成功，请登录", false);
                TxtUser.Text = user;
                GoLogin_Click(this, null!);
                SetMsg(TxtLoginMsg, "注册成功，请输入密码登录", false);
            }
        }
        catch (Exception ex)
        {
            SetMsg(TxtRegMsg, ex.Message, true);
        }
        finally
        {
            BtnRegister.IsEnabled = true;
            BtnRegister.Content = "注 册";
        }
    }

    private static void SetMsg(TextBlock tb, string text, bool error)
    {
        tb.Text = text;
        tb.Foreground = (Brush)(error
            ? Application.Current.FindResource("DangerBrush")
            : Application.Current.FindResource("OkBrush"));
    }
}
