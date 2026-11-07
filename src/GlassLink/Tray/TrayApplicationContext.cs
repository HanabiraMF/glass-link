using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using GlassLink.App;
using GlassLink.Logging;

namespace GlassLink.Tray;

/// <summary>
/// Windows tray host for the silent WinExe. The ASP.NET Core server runs in the
/// background; this context owns the tray icon and menu and keeps the WinForms
/// message loop alive.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly GlassLinkRuntime _runtime;
    private readonly string _desktopUrl;
    private readonly string _mobileUrl;
    private readonly ILogger _logger;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private bool _disposed;

    public TrayApplicationContext(GlassLinkRuntime runtime, string desktopUrl, string mobileUrl, ILogger logger)
    {
        _runtime = runtime;
        _desktopUrl = desktopUrl;
        _mobileUrl = mobileUrl;
        _logger = logger;

        _statusItem = new ToolStripMenuItem("当前状态：正在启动…");
        _statusItem.Click += (_, _) => ShowStatus();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("打开设置面板", null, (_, _) => OpenUrl(_desktopUrl));
        _menu.Items.Add("打开手机端页面", null, (_, _) => OpenUrl(_mobileUrl));
        _menu.Items.Add("打开日志目录", null, (_, _) => OpenLogFolder());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出程序", null, (_, _) => ExitApplication());
        _menu.Opening += (_, _) => UpdateStatus();

        _notifyIcon = new NotifyIcon
        {
            Text = "GlassLink",
            Visible = true,
            ContextMenuStrip = _menu,
            Icon = LoadTrayIcon()
        };
        _notifyIcon.DoubleClick += (_, _) => OpenUrl(_desktopUrl);

        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        UpdateStatus();
        _logger.LogInformation("Tray host started. Desktop URL: {DesktopUrl}", desktopUrl);
    }

    private void UpdateStatus()
    {
        try
        {
            var status = _runtime.GetStatus();
            var state = status.Streaming ? "串流中" : status.StreamState switch
            {
                "starting" => "启动中",
                "recovering" => "恢复中",
                "stopping" => "停止中",
                "error" => "错误",
                _ => "待机"
            };
            var text = $"{state} · {status.Clients} 个客户端 · {status.Stats.Fps:F0} FPS";
            if (text.Length > 55) text = text[..55];
            _statusItem.Text = "当前状态：" + text;
            _notifyIcon.Text = Truncate("GlassLink · " + text, 63);
        }
        catch
        {
            // A status probe must never crash the tray message loop.
        }
    }

    private void ShowStatus()
    {
        try
        {
            var status = _runtime.GetStatus();
            var controller = status.Controller;
            var video = status.Video;
            var text =
                $"串流状态：{(status.Streaming ? "运行中" : status.StreamState)}\r\n" +
                $"客户端：{status.Clients}\r\n" +
                $"帧率：{status.Stats.Fps:F1} FPS\r\n" +
                $"码率：{status.Stats.MegabitsPerSecond:F1} Mbps\r\n" +
                $"输入层：{controller.ActiveDisplayName}\r\n" +
                (controller.FallbackReason is null ? "" : $"回退原因：{controller.FallbackReason}\r\n") +
                (video is null ? "" : $"编码：{video.EncoderName}\r\n") +
                $"本地页面：{_desktopUrl}";
            MessageBox.Show(text, "GlassLink 当前状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "GlassLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open URL {Url}.", url);
            MessageBox.Show($"无法打开浏览器：{ex.Message}", "GlassLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenLogFolder()
    {
        var path = FileLoggerProvider.LogFilePath;
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show("日志文件尚未初始化。", "GlassLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open log folder.");
        }
    }

    private void ExitApplication()
    {
        _logger.LogInformation("Tray exit requested.");
        _statusTimer.Stop();
        _notifyIcon.Visible = false;
        Application.Exit();
    }

    private static Icon LoadTrayIcon()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Assets", "glasslink.ico");
        try
        {
            if (File.Exists(candidate)) return new Icon(candidate);
        }
        catch { }
        return SystemIcons.Application;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..Math.Max(0, max)];

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _statusTimer.Stop();
            _statusTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
