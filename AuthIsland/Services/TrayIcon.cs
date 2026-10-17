using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace AuthIsland.Services;

/// <summary>
/// 托盘图标。存在的意义有两个：
///   1. 胶囊休眠/隐藏后，用户还有个"入口"能把它叫回来 —— 否则一旦自动隐身就再也找不到；
///   2. 提供不依赖右键菜单的退出 / 暂停通道。
///
/// 图标不放资源文件，直接画一个（黑底 + 绿点），省掉 .ico 二进制的维护成本。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notify;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Icon _icon;
    private IntPtr _hIcon;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<bool>? PauseChanged;

    public TrayIcon(bool paused = false)
    {
        _icon = BuildIcon(out _hIcon);

        var show = new Forms.ToolStripMenuItem("显示胶囊");
        show.Click += (_, _) => ShowRequested?.Invoke();

        _pauseItem = new Forms.ToolStripMenuItem("暂停拦截")
        {
            CheckOnClick = true,
            Checked = paused
        };
        _pauseItem.Click += (_, _) => PauseChanged?.Invoke(_pauseItem.Checked);

        var quit = new Forms.ToolStripMenuItem("退出 AuthIsland");
        quit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(show);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quit);

        _notify = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "AuthIsland · 授权胶囊",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowRequested?.Invoke();
        };
    }

    public void SetPaused(bool paused)
    {
        if (_pauseItem.Checked != paused) _pauseItem.Checked = paused;
    }

    private static Icon BuildIcon(out IntPtr handle)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var body = new SolidBrush(Color.FromArgb(255, 16, 16, 22));
            g.FillEllipse(body, 2, 2, 28, 28);

            using var ring = new Pen(Color.FromArgb(80, 255, 255, 255), 1.2f);
            g.DrawEllipse(ring, 2.6f, 2.6f, 26.8f, 26.8f);

            using var dot = new SolidBrush(Color.FromArgb(255, 74, 222, 128));
            g.FillEllipse(dot, 11, 11, 10, 10);
        }

        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        try { _notify.Visible = false; } catch { }
        try { _notify.Dispose(); } catch { }

        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
