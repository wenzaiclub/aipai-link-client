using System.IO;
using System.Text;

namespace AppiieNet.Client.Services;

/// <summary>
/// 客户端日志。
///
/// 组网引擎（easytier-core）的输出以前是直接丢掉的——进程用 CreateNoWindow 起，
/// 输出没有重定向，出了连接问题只能靠猜。现在统一收进
/// %APPDATA%\艾派组网\logs\，界面上的「日志」窗口能看，也可以直接去翻文件。
///
/// 两个来源：
///   client  客户端自己的操作（登录、连接、断开、更新…）
///   engine  组网引擎的原样输出
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int KeepLinesAfterTrim = 2000;

    public static string Dir { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("APPIENET_DATA_DIR") is { Length: > 0 } logRoot
            ? logRoot
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "艾派组网", "logs");

    public static string FileOf(string source) => Path.Combine(Dir, source + ".log");

    /// <summary>写一行。写失败就算了，绝不能因为记日志把功能带崩。</summary>
    public static void Write(string source, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line.TrimEnd();
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FileOf(source), text + "\n", Utf8);
                TrimIfTooBig(source);
            }
        }
        catch
        {
            // 日志写不进去不影响主流程
        }
    }

    public static void Client(string line) => Write("client", line);

    private static void TrimIfTooBig(string source)
    {
        var path = FileOf(source);
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length <= MaxFileBytes)
        {
            return;
        }
        var keep = ReadTail(path, KeepLinesAfterTrim);
        File.WriteAllLines(path, keep, Utf8);
    }

    /// <summary>取文件末尾若干行；只读末尾 256KB，日志大了也不卡</summary>
    public static string[] ReadTail(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, 256 * 1024);
            var partial = take < fs.Length;
            fs.Seek(-take, SeekOrigin.End);
            using var reader = new StreamReader(fs, Utf8);
            var lines = reader.ReadToEnd()
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToArray();
            if (partial && lines.Length > 1)
            {
                lines = lines.Skip(1).ToArray();   // 第一行多半是半截
            }
            return lines.Length <= maxLines ? lines : lines[^maxLines..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>清空某个来源的日志</summary>
    public static void Clear(string source)
    {
        try
        {
            lock (Gate)
            {
                var path = FileOf(source);
                if (File.Exists(path))
                {
                    File.WriteAllText(path, "", Utf8);
                }
            }
        }
        catch
        {
            // 清不掉就留着
        }
    }
}
