using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Picky.Native;

/// <summary>
/// Places windows using physical pixels.
///
/// WPF's <c>Window.Left/Top</c> are device-independent units interpreted against a single
/// monitor's DPI, and <c>SystemParameters.WorkArea</c> only ever describes the *primary*
/// display. Together that means the usual "dock to the lower-right corner" arithmetic puts
/// popups on the wrong monitor — or at the wrong offset — as soon as a second display with
/// a different scale factor is attached.
/// </summary>
internal static class WindowPlacement
{
    /// <summary>Docks <paramref name="window"/> flush into the lower-right of a work area.</summary>
    /// <param name="workAreaPx">Target work area in physical pixels (excludes the taskbar).</param>
    public static void DockToLowerRight(Window window, Rectangle workAreaPx)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Park it on the target monitor first, so GetDpiForWindow reports *that* monitor's
        // scale rather than the one the window happened to be created on.
        SetWindowPos(hwnd, IntPtr.Zero, workAreaPx.X, workAreaPx.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        var (width, height) = PhysicalSize(window, hwnd);

        SetWindowPos(hwnd, IntPtr.Zero,
            workAreaPx.Right - width, workAreaPx.Bottom - height,
            width, height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Centres <paramref name="window"/> horizontally at the top of a work area.</summary>
    public static void CenterTop(Window window, Rectangle workAreaPx, int marginPx)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, IntPtr.Zero, workAreaPx.X, workAreaPx.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        var (width, _) = PhysicalSize(window, hwnd);

        SetWindowPos(hwnd, IntPtr.Zero,
            workAreaPx.X + (workAreaPx.Width - width) / 2, workAreaPx.Y + marginPx, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// The window's size in physical pixels for the monitor it currently sits on. Prefers the
    /// explicit DIP size, falling back to the measured size for SizeToContent windows.
    /// </summary>
    private static (int Width, int Height) PhysicalSize(Window window, IntPtr hwnd)
    {
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        double dipWidth = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
        double dipHeight = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;

        // A never-shown SizeToContent window can report 0; leave it to Windows in that case.
        if (dipWidth <= 0 || dipHeight <= 0)
        {
            GetWindowRect(hwnd, out var current);
            return (current.Right - current.Left, current.Bottom - current.Top);
        }

        return ((int)Math.Ceiling(dipWidth * scale), (int)Math.Ceiling(dipHeight * scale));
    }

    // --- Win32 ---

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
