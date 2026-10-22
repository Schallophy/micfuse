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
    private readonly Icon _appIcon;
    private readonly IntPtr _iconHandle;
    private bool _active;
    private bool _restarting;
    private bool? _darkTheme;
    private volatile bool _themeDirty;

    public ApplicationContext Context => _context;

    public SystemTray(AudioEngine engine, WebUi web, Task server, CancellationToken cancel)
    {
        _engine = engine;
        _web = web;
        _server = server;
        _cancel = cancel;
        _appIcon = CreateIcon(out _iconHandle);

        _openItem = new ToolStripMenuItem("打开 MicFuse", null, (_, _) => _web.OpenPage());
        _restartItem = new ToolStripMenuItem("重启引擎", null, async (_, _) => await RestartAsync());
        _exitItem = new ToolStripMenuItem("退出", null, (_, _) => _context.ExitThread());
        _menu = new ContextMenuStrip
        {
            // The default menu reserves a light image gutter and a check gutter; neither is used here.
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(4),
        };
        foreach (var item in new[] { _openItem, _restartItem, _exitItem })
            item.Padding = new Padding(6, 3, 6, 3);
        _menu.Items.Add(_openItem);
        _menu.Items.Add(_restartItem);
        _menu.Items.Add(_exitItem);
        SyncMenuTheme();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _icon = new NotifyIcon
        {
            Text = BuildTooltip(false, web.Url ?? ""),
            Icon = _appIcon,
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
            await _engine.RestartAllLines();
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
        if (_themeDirty)
        {
            _themeDirty = false;
            SyncMenuTheme();
        }

        bool active;
        try
        {
            active = _engine.Snapshot().Running;
        }
        catch (Exception)
        {
            active = false;
        }

        if (active != _active)
        {
            _active = active;
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

    /// <summary>Raised on a SystemEvents thread; the tick applies it on the UI thread.</summary>
    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General
            or Microsoft.Win32.UserPreferenceCategory.VisualStyle)
            _themeDirty = true;
    }

    /// <summary>Follows the Windows app theme; the palette mirrors the web UI's light/dark surfaces.</summary>
    private void SyncMenuTheme()
    {
        bool dark = IsDarkTheme();
        if (dark == _darkTheme) return;
        _darkTheme = dark;
        var palette = dark ? MenuPalette.Dark : MenuPalette.Light;
        _menu.BackColor = palette.Background;
        _menu.ForeColor = palette.Foreground;
        _menu.Renderer = new ToolStripProfessionalRenderer(new MenuColorTable(palette)) { RoundedEdges = false };
        foreach (ToolStripItem item in _menu.Items) item.ForeColor = palette.Foreground;
    }

    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class MenuPalette
    {
        public static readonly MenuPalette Dark = new()
        {
            Background = Color.FromArgb(36, 35, 32),
            Foreground = Color.FromArgb(243, 239, 230),
            Hover = Color.FromArgb(46, 44, 41),
            Border = Color.FromArgb(58, 55, 51),
        };

        public static readonly MenuPalette Light = new()
        {
            Background = Color.FromArgb(255, 255, 255),
            Foreground = Color.FromArgb(31, 30, 29),
            Hover = Color.FromArgb(246, 243, 236),
            Border = Color.FromArgb(232, 228, 219),
        };

        public Color Background { get; init; }
        public Color Foreground { get; init; }
        public Color Hover { get; init; }
        public Color Border { get; init; }
    }

    private sealed class MenuColorTable : ProfessionalColorTable
    {
        private readonly MenuPalette _palette;

        public MenuColorTable(MenuPalette palette) => _palette = palette;

        public override Color ToolStripDropDownBackground => _palette.Background;
        public override Color ImageMarginGradientBegin => _palette.Background;
        public override Color ImageMarginGradientMiddle => _palette.Background;
        public override Color ImageMarginGradientEnd => _palette.Background;
        public override Color MenuBorder => _palette.Border;
        public override Color ToolStripBorder => _palette.Border;
        public override Color MenuItemSelected => _palette.Hover;
        public override Color MenuItemSelectedGradientBegin => _palette.Hover;
        public override Color MenuItemSelectedGradientEnd => _palette.Hover;
        public override Color MenuItemBorder => _palette.Hover;
        public override Color MenuItemPressedGradientBegin => _palette.Hover;
        public override Color MenuItemPressedGradientMiddle => _palette.Hover;
        public override Color MenuItemPressedGradientEnd => _palette.Hover;
        public override Color SeparatorDark => _palette.Border;
        public override Color SeparatorLight => _palette.Background;
    }

    private static Icon CreateIcon(out IntPtr handle)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color background = Color.FromArgb(217, 119, 87);
            Color foreground = Color.White;

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
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _openItem.Dispose();
        _restartItem.Dispose();
        _exitItem.Dispose();
        _appIcon.Dispose();
        _context.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
