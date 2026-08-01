using System.Runtime.InteropServices;

namespace Picky.Native;

/// <summary>
/// Monitor geometry in <b>physical pixels</b>, read straight from Win32.
///
/// Why not <c>System.Windows.Forms.Screen</c> or <c>SystemParameters.VirtualScreen*</c>?
/// Both hand back values that have been divided by a *single* DPI scale, so on a
/// mixed-DPI desktop (e.g. a 175% 4K panel next to a 100% 1080p one) the numbers are
/// internally inconsistent — positions in real pixels, sizes in scaled units. Capture
/// has to happen in real pixels, so everything here is raw and unscaled. This is only
/// correct because the app manifest declares <c>PerMonitorV2</c> awareness.
/// </summary>
internal static class MonitorInfo
{
    /// <summary>One physical display.</summary>
    /// <param name="Index">1-based, ordered left-to-right then top-to-bottom.</param>
    internal sealed record Monitor(
        int Index,
        string DeviceName,
        Rectangle Bounds,
        Rectangle WorkArea,
        bool IsPrimary)
    {
        /// <summary>Menu-friendly label, e.g. "Display 2 (primary) — 1920 × 1200".</summary>
        public string Label =>
            $"Display {Index}{(IsPrimary ? " (primary)" : "")} — {Bounds.Width} × {Bounds.Height}";
    }

    /// <summary>
    /// Bounding box of every monitor combined, in physical pixels. The origin can be
    /// negative when a display sits left of / above the primary one.
    /// </summary>
    public static Rectangle VirtualScreen => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>All displays, ordered left-to-right then top-to-bottom.</summary>
    public static List<Monitor> All()
    {
        var found = new List<(Rectangle Bounds, Rectangle Work, bool Primary, string Device)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                found.Add((
                    ToRectangle(info.rcMonitor),
                    ToRectangle(info.rcWork),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    info.szDevice ?? string.Empty));
            }

            return true; // keep enumerating
        }, IntPtr.Zero);

        return found
            .OrderBy(m => m.Bounds.X)
            .ThenBy(m => m.Bounds.Y)
            .Select((m, i) => new Monitor(i + 1, m.Device, m.Bounds, m.Work, m.Primary))
            .ToList();
    }

    /// <summary>The display containing <paramref name="point"/> (physical px), else the primary one.</summary>
    public static Monitor FromPoint(System.Drawing.Point point)
    {
        var monitors = All();
        return monitors.FirstOrDefault(m => m.Bounds.Contains(point))
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors.FirstOrDefault()
            ?? new Monitor(1, string.Empty, VirtualScreen, VirtualScreen, true);
    }

    /// <summary>The display the mouse pointer is currently on.</summary>
    public static Monitor FromCursor() => FromPoint(CursorPosition);

    /// <summary>Mouse pointer position in physical pixels.</summary>
    public static System.Drawing.Point CursorPosition
        => GetCursorPos(out var p) ? new System.Drawing.Point(p.X, p.Y) : System.Drawing.Point.Empty;

    private static Rectangle ToRectangle(RECT r)
        => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    // --- Win32 ---

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcClip, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
