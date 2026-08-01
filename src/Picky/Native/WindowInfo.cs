using System.Runtime.InteropServices;
using System.Text;

namespace Picky.Native;

/// <summary>
/// Facts about top-level windows, in <b>physical pixels</b>.
///
/// Shared by the capture overlay's click-to-grab auto-detect and by <c>--probe</c>, so the
/// diagnostic reports exactly the geometry a real capture would use.
/// </summary>
internal static class WindowInfo
{
    /// <summary>
    /// The window's <b>visible</b> bounds — the frame the user actually sees.
    ///
    /// <c>GetWindowRect</c> includes the invisible DWM resize border, which on Windows 10/11 hangs
    /// roughly 7px off the left, right and bottom of a normal window and scales with DPI (12px at
    /// 175%). Capturing that rectangle yields an image visibly wider and taller than the window,
    /// padded with a strip of whatever happened to be behind it.
    /// </summary>
    public static bool TryVisibleBounds(IntPtr hwnd, out Rectangle bounds)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0
            && frame.Right > frame.Left && frame.Bottom > frame.Top)
        {
            bounds = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
            return true;
        }

        // Not every window is DWM-composited (some console/legacy windows aren't); fall back.
        if (GetWindowRect(hwnd, out var raw))
        {
            bounds = Rectangle.FromLTRB(raw.Left, raw.Top, raw.Right, raw.Bottom);
            return !bounds.IsEmpty;
        }

        bounds = Rectangle.Empty;
        return false;
    }

    /// <summary>Bounds as Windows reports them, including the invisible resize border.</summary>
    public static bool TryOuterBounds(IntPtr hwnd, out Rectangle bounds)
    {
        if (GetWindowRect(hwnd, out var raw))
        {
            bounds = Rectangle.FromLTRB(raw.Left, raw.Top, raw.Right, raw.Bottom);
            return true;
        }

        bounds = Rectangle.Empty;
        return false;
    }

    /// <summary>
    /// True when DWM has cloaked the window — most commonly because it lives on another virtual
    /// desktop. Such windows still report <c>IsWindowVisible</c> and are not minimised, so this
    /// check is what stops them being treated as on-screen.
    /// </summary>
    public static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>True for palette / tool windows, which shouldn't be auto-detect targets.</summary>
    public static bool IsToolWindow(IntPtr hwnd)
        => (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0;

    public static string GetTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>Walks top-level windows in Z-order, front to back.</summary>
    public static void ForEachTopLevel(Func<IntPtr, bool> visit)
        => EnumWindows((hwnd, _) => visit(hwnd), IntPtr.Zero);

    public static bool IsVisible(IntPtr hwnd) => IsWindowVisible(hwnd);

    public static bool IsMinimised(IntPtr hwnd) => IsIconic(hwnd);

    // --- Win32 ---

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
