using System.IO;
using System.Windows;
using AiPai.Compact.Services;
using AppiieNet.Client.Services;

namespace AiPai.Compact;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 紧凑版用自己独立的配置/日志目录，避免和主线客户端互相覆盖
        Environment.SetEnvironmentVariable("APPIENET_DATA_DIR",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "艾派组网紧凑版"));
        AppLog.Write("client", "客户端启动 v1.0.10");
        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }
}
