using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiPai.Compact.Services;
using AppiieNet.Client.Models;
using AppiieNet.Client.Services;

namespace AiPai.Compact.Views;

/// <summary>
/// 转让房主：房主把组网交给同一个组网里的另一个账号。
/// 转让只改账号身份，组网本身、适配码、已分配的固定 IP 都不动。
/// </summary>
public partial class TransferOwnerWindow : Window
{
    private readonly NetworkSummary _net;
    private readonly string _token;
    private int _targetUserId;
    private readonly List<RadioButton> _radios = new();

    public TransferOwnerWindow(NetworkSummary net, string token)
    {
        InitializeComponent();
        _net = net;
        _token = token;
        TxtNet.Text = $"组网：{net.Name}（{net.Code}）";
        BuildUsers();
        PositionWindow();
        Loaded += (_, __) => Activate();
    }

    /// <summary>只列「别的账号」，同一个账号在自己几台设备上都算房主，转让给自己没意义</summary>
    private void BuildUsers()
    {
        var st = AppState.Current;
        var myUserId = st.Login?.User.Id ?? 0;
        var members = st.MembersOf(_net.Id)
            .Where(m => m.UserId != myUserId && m.UserId > 0)
            .GroupBy(m => m.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Name = g.Select(x => x.Username).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? ("用户 " + g.Key),
                Devices = g.Count(),
                Online = g.Count(x => x.Online),
            })
            .OrderByDescending(x => x.Online)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (members.Count == 0)
        {
            UserList.Children.Add(new TextBlock
            {
                Text = "这个组网里还没有别的账号。\n让对方先用他自己的账号输入适配码加入，然后再来转让。",
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 8, 8, 8),
                Foreground = (Brush)FindResource("TextDim"),
            });
            BtnTransfer.IsEnabled = false;
            return;
        }

        foreach (var u in members)
        {
            var rb = new RadioButton
            {
                GroupName = "transferTarget",
                Margin = new Thickness(6, 5, 6, 5),
                Cursor = Cursors.Hand,
                Foreground = (Brush)FindResource("TextPrimary"),
                Tag = u.UserId,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock
            {
                Text = u.Name,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"　{u.Devices} 台设备（{u.Online} 在线）",
                FontSize = 11,
                Foreground = (Brush)FindResource("TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            rb.Content = row;
            var uid = u.UserId;
            rb.Checked += (_, __) => _targetUserId = uid;
            _radios.Add(rb);
            UserList.Children.Add(rb);
        }

        _radios[0].IsChecked = true;
        _targetUserId = members[0].UserId;
    }

    private void PositionWindow()
    {
        var wa = SystemParameters.WorkArea;
        double left, top;
        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            left = owner.Left + (owner.Width - Width) / 2;
            top = owner.Top + (owner.Height - 320) / 2;
        }
        else { left = wa.Right - Width - 60; top = wa.Bottom - 400; }
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

    private async void Transfer_Click(object sender, RoutedEventArgs e)
    {
        if (_targetUserId <= 0) { SetMsg("请先选择要转让的账号", true); return; }

        BtnTransfer.IsEnabled = false;
        BtnTransfer.Content = "转让中…";
        try
        {
            await AppState.Current.Api.TransferNetworkAsync(_net.Id, _targetUserId, _token);
            AppLog.Client($"[转让房主] 「{_net.Name}」转给用户 {_targetUserId}");
            var st = AppState.Current;
            await st.RefreshNetworksAsync();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Client($"[转让房主] 「{_net.Name}」失败：{ex.Message}");
            SetMsg(ex.Message, true);
            BtnTransfer.IsEnabled = true;
            BtnTransfer.Content = "确认转让";
        }
    }

    private void SetMsg(string text, bool error)
    {
        TxtMsg.Text = text;
        TxtMsg.Foreground = (Brush)FindResource(error ? "DangerBrush" : "OkBrush");
    }
}
