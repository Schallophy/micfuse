using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MicFuse;

internal sealed class SystemTray : IDisposable
{
    private readonly WebUi _web;
    private readonly AudioEngine _engine;
    private readonly Task _server;
    private readonly CancellationToken _cancel;
    private readonly ApplicationContext _context = new();
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Icon _idleIcon;
    private readonly Icon _activeIcon;
    private readonly IntPtr _idleHandle;
    private readonly IntPtr _activeHandle;
    private bool _showActive;
    private bool _restarting;

    public ApplicationContext Context => _context;

    public SystemTray(AudioEngine engine, WebUi web, Task server, CancellationToken cancel)
    {
        _engine = engine;
        _web = web;
        _server = server;
        _cancel = cancel;
        _idleIcon = CreateIcon(false, out _idleHandle);
        _activeIcon = CreateIcon(true, out _activeHandle);

        _openItem = new ToolStripMenuItem("打开 MicFuse", null, (_, _) => _web.OpenPage());
        _restartItem = new ToolStripMenuItem("重启引擎", null, async (_, _) => await RestartAsync());
        _exitItem = new ToolStripMenuItem("退出", null, (_, _) => _context.ExitThread());
        _menu = new ContextMenuStrip();
        _menu.Items.Add(_openItem);
        _menu.Items.Add(_restartItem);
        _menu.Items.Add(_exitItem);

        _icon = new NotifyIcon
        {
            Text = BuildTooltip(false, web.Url ?? ""),
            Icon = _idleIcon,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => _web.OpenPage();

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private async Task RestartAsync()
    {
        if (_restarting) return;
        _restarting = true;
        _restartItem.Enabled = false;
        _restartItem.Text = "重启中…";
        try
        {
            await _engine.RestartCurrentAsync();
        }
        catch (Exception ex)
        {
            Log.Line("tray restart failed: " + ex.Message);
        }
        finally
        {
            _restartItem.Text = "重启引擎";
            _restartItem.Enabled = true;
            _restarting = false;
        }
    }

    private void Tick()
    {
        bool active;
        try
        {
            active = _engine.Snapshot().Running;
        }
        catch (Exception)
        {
            active = false;
        }

        if (active != _showActive)
        {
            _showActive = active;
            _icon.Icon = active ? _activeIcon : _idleIcon;
            _icon.Text = BuildTooltip(active, _web.Url ?? "");
        }

        if (_cancel.IsCancellationRequested || _server.IsCompleted)
        {
            _timer.Stop();
            _context.ExitThread();
        }
    }

    private static string BuildTooltip(bool active, string url) =>
        active ? $"MicFuse — 混音运行中\n{url}" : $"MicFuse — 已停止\n{url}";

    private static Icon CreateIcon(bool active, out IntPtr handle)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color background = active ? Color.FromArgb(217, 119, 87) : Color.FromArgb(31, 30, 29);
            Color foreground = active ? Color.White : Color.FromArgb(250, 249, 245);

            using var backgroundBrush = new SolidBrush(background);
            using var foregroundBrush = new SolidBrush(foreground);
            using var pen = new Pen(foreground, 2f);

            g.FillEllipse(backgroundBrush, 1, 1, 30, 30);
            g.FillEllipse(foregroundBrush, 12, 4, 8, 5);
            g.FillRectangle(foregroundBrush, 12, 6, 8, 8);
            g.FillEllipse(foregroundBrush, 12, 12, 8, 5);
            g.DrawArc(pen, 10, 13, 12, 11, 0, 180);
            g.DrawLine(pen, 16, 24, 16, 27);
            g.DrawLine(pen, 10, 27, 22, 27);
        }

        handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _openItem.Dispose();
        _restartItem.Dispose();
        _exitItem.Dispose();
        _idleIcon.Dispose();
        _activeIcon.Dispose();
        _context.Dispose();
        if (_idleHandle != IntPtr.Zero) DestroyIcon(_idleHandle);
        if (_activeHandle != IntPtr.Zero) DestroyIcon(_activeHandle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
