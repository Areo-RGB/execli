using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ExecMcp.Core;

public static class WindowInspector
{
    public static IReadOnlyList<WindowInfo> Enumerate()
    {
        var result = new List<WindowInfo>();
        Native.EnumWindows((hwnd, _) =>
        {
            var length = Native.GetWindowTextLength(hwnd);
            var builder = new StringBuilder(length + 1);
            _ = Native.GetWindowText(hwnd, builder, builder.Capacity);
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            result.Add(new WindowInfo(hwnd, checked((int)pid), builder.ToString(), Native.IsWindowVisible(hwnd), Native.IsIconic(hwnd)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static async Task<WindowInfo> ResolveAsync(string? jobId = null, int? pid = null, string? title = null, nint? hwnd = null, CancellationToken cancellationToken = default)
    {
        if (hwnd is not null)
        {
            var match = Enumerate().FirstOrDefault(window => window.Hwnd == hwnd.Value);
            return match ?? throw new KeyNotFoundException($"Window not found: 0x{hwnd.Value:x}");
        }
        if (jobId is not null)
        {
            var state = await new StateStore().ReadAsync(cancellationToken).ConfigureAwait(false);
            pid = JobService.Find(state, jobId).Pid ?? throw new InvalidOperationException($"Job {jobId} does not have a process yet");
        }
        var windows = Enumerate().Where(window => window.Visible && !string.IsNullOrWhiteSpace(window.Title));
        if (pid is not null) windows = windows.Where(window => window.Pid == pid.Value);
        if (!string.IsNullOrWhiteSpace(title)) windows = windows.Where(window => window.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        return windows.FirstOrDefault() ?? throw new KeyNotFoundException("No matching top-level window was found");
    }

    internal static class Native
    {
        internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(nint hwnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(nint hwnd, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
        [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint hdc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(nint dest, int xDest, int yDest, int width, int height, nint source, int xSource, int ySource, uint rop);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect rect, int size);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
}

public static class WindowCapture
{
    private const int SwRestore = 9;
    private const int SwMinimize = 6;
    private const int DwmwaExtendedFrameBounds = 9;
    private const uint PwRenderFullContent = 2;
    private const uint SrcCopy = 0x00CC0020;

    public static async Task<Dictionary<string, object?>> CaptureAsync(WindowInfo window, string outputPath, bool allowForeground = true, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var wasMinimized = window.Minimized;
        if (wasMinimized)
        {
            _ = WindowInspector.Native.ShowWindowAsync(window.Hwnd, SwRestore);
            await Task.Delay(175, cancellationToken).ConfigureAwait(false);
        }
        if (allowForeground)
        {
            _ = WindowInspector.Native.SetForegroundWindow(window.Hwnd);
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var rect = GetBounds(window.Hwnd);
            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try
            {
                var printed = WindowInspector.Native.PrintWindow(window.Hwnd, hdc, PwRenderFullContent);
                if (!printed)
                {
                    var screen = WindowInspector.Native.GetDC(IntPtr.Zero);
                    if (screen == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC failed");
                    try
                    {
                        if (!WindowInspector.Native.BitBlt(hdc, 0, 0, width, height, screen, rect.Left, rect.Top, SrcCopy))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Window capture failed");
                    }
                    finally { _ = WindowInspector.Native.ReleaseDC(IntPtr.Zero, screen); }
                }
            }
            finally { graphics.ReleaseHdc(hdc); }
            bitmap.Save(full, ImageFormat.Png);
            var bytes = new FileInfo(full).Length;
            return new Dictionary<string, object?>
            {
                ["path"] = full, ["bytes"] = bytes, ["hwnd"] = (long)window.Hwnd, ["pid"] = window.Pid,
                ["title"] = window.Title, ["width"] = width, ["height"] = height
            };
        }
        finally
        {
            if (wasMinimized) _ = WindowInspector.Native.ShowWindowAsync(window.Hwnd, SwMinimize);
        }
    }

    private static WindowInspector.Rect GetBounds(nint hwnd)
    {
        if (WindowInspector.Native.DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<WindowInspector.Rect>()) == 0)
            return frame;
        if (!WindowInspector.Native.GetWindowRect(hwnd, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed");
        return rect;
    }
}
