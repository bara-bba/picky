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

    /// <summary>
    /// Brings <paramref name="window"/> to the front and gives it focus, even from a tray click.
    /// </summary>
    /// <remarks>
    /// <c>Window.Activate()</c> alone is refused by Windows' foreground lock when Picky isn't the
    /// foreground app (a tray click leaves the taskbar in front), and the window then opens behind
    /// whatever app was active. Briefly attaching to the foreground thread's input queue lets
    /// <c>SetForegroundWindow</c> succeed. Returns true when the window ended up in the foreground.
    /// </remarks>
    public static bool ForceForeground(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        var foreground = GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint ownThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != ownThread
            && AttachThreadInput(ownThread, foregroundThread, true);

        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(ownThread, foregroundThread, false);
            }
        }

        window.Activate();
        return GetForegroundWindow() == hwnd;
    }

    // --- Win32 ---

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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
