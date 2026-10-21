using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MicFuse;

internal static class IconCache
{
    private const uint WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int GCLP_HICON = -14;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<int, IntPtr> Windows = new();
    private static readonly ConcurrentDictionary<IntPtr, byte[]> WindowIcons = new();
    private static readonly byte[] Empty = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    public static void SetWindow(int pid, IntPtr hwnd)
    {
        if (pid > 0 && hwnd != IntPtr.Zero) Windows[pid] = hwnd;
    }

    public static byte[] ForPid(int pid)
    {
        if (pid <= 0) return Empty;
        try
        {
            using var process = Process.GetProcessById(pid);
            string? exe = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                return Cache.GetOrAdd(exe, ExtractFromExe);
        }
        catch (Exception)
        {
            // protected/elevated processes deny MainModule; fall back to the window icon below
        }
        return Windows.TryGetValue(pid, out var hwnd) ? WindowIcons.GetOrAdd(hwnd, ExtractFromWindow) : Empty;
    }

    private static byte[] ExtractFromExe(string exe)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exe);
            return icon is null ? Empty : ToPng(icon);
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    private static byte[] ExtractFromWindow(IntPtr hwnd)
    {
        try
        {
            IntPtr handle = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
            if (handle == IntPtr.Zero) handle = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
            if (handle == IntPtr.Zero) handle = GetClassLongPtr(hwnd, GCLP_HICON);
            if (handle == IntPtr.Zero) return Empty;
            using var icon = Icon.FromHandle(handle); // does not own the window's icon handle
            return ToPng(icon);
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    private static byte[] ToPng(Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
