using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace Picky;

/// <summary>
/// Draws a slowly pulsing ("breathing") frame around the region currently being recorded.
///
/// <para><b>Why four strips instead of one outlined window.</b> The recorder grabs the screen, so
/// anything painted <i>over</i> the region ends up in the video. These strips sit entirely in the
/// pixels just <i>outside</i> the recorded rect, so the frame is visible on screen but never
/// captured. Four small opaque windows also avoid a large layered window hovering over the recorded
/// area — that would cost DWM compositing work on exactly the pixels being captured, and risks the
/// grab picking up its blended pixels.</para>
///
/// <para>Placement is done with <c>SetWindowPos</c> in physical pixels for the same reason as the
/// capture overlay: WPF's Left/Top are DIPs against one monitor's DPI and can't express a position
/// on a mixed-DPI desktop.</para>
/// </summary>
internal sealed class RecordingBorder
{
    /// <summary>Recording red, matching the palette's red swatch and the record bar's dot.</summary>
    private static readonly MediaColor Bright = MediaColor.FromRgb(0xE8, 0x11, 0x23);

    /// <summary>Trough of the breathing pulse — dim, but still unmistakably red.</summary>
    private static readonly MediaColor Dim = MediaColor.FromRgb(0x6E, 0x0A, 0x11);

    /// <summary>Half-cycle; with AutoReverse this gives a ~2.2s in-and-out breath.</summary>
    private static readonly Duration BreathHalfCycle = new(TimeSpan.FromMilliseconds(1100));

    private const int Thickness = 3;

    private readonly List<Window> _strips = new();

    /// <summary>
    /// The animated fill, shared by all four strips so one animation drives the whole frame in step.
    /// Recreated per <see cref="Show"/> and released in <see cref="Hide"/> — a long-lived animated
    /// static brush would keep ticking after recording stopped.
    /// </summary>
    private SolidColorBrush? _frameBrush;

    public bool IsVisible => _strips.Count > 0;

    /// <summary>Frames <paramref name="regionPx"/> (physical pixels) without covering any of it.</summary>
    public void Show(Rectangle regionPx)
    {
        Hide();

        if (regionPx.Width < 1 || regionPx.Height < 1)
        {
            return;
        }

        int x = regionPx.X;
        int y = regionPx.Y;
        int w = regionPx.Width;
        int h = regionPx.Height;
        const int t = Thickness;

        // Strictly outside the recorded rect. Top and bottom run the full outer width. The left and
        // right strips span the inner height plus a 1px overlap into the top and bottom strips at
        // each end, so the four butt-joints are covered without stacking a full strip's worth of
        // paint in the corners. The 1px overlap is what closes the hairline seams that appear when
        // four independently DPI-rounded top-level windows round their edges apart on a scaled
        // display; keeping it to 1px avoids a visibly darker double-painted corner during the breath.
        const int o = 1;
        var edges = new[]
        {
            new Rectangle(x - t, y - t, w + 2 * t, t),         // top
            new Rectangle(x - t, y + h, w + 2 * t, t),         // bottom
            new Rectangle(x - t, y - o, t, h + 2 * o),         // left
            new Rectangle(x + w, y - o, t, h + 2 * o),         // right
        };

        // One brush for all four strips, so the whole frame breathes in step.
        _frameBrush = new SolidColorBrush(Bright);

        foreach (var edge in edges)
        {
            _strips.Add(CreateStrip(edge, _frameBrush));
        }

        StartBreathing(_frameBrush);
    }

    /// <summary>
    /// Pulses the fill between <see cref="Bright"/> and <see cref="Dim"/>.
    ///
    /// Animating the <i>colour</i> rather than <c>Window.Opacity</c> is deliberate: WPF only honours
    /// window opacity when <c>AllowsTransparency</c> is true, and turning that on would make these
    /// layered windows and drop them into software rendering — for a frame that sits alongside a live
    /// screen recording, that's a cost worth avoiding. Colour animation keeps them opaque and cheap.
    /// </summary>
    private static void StartBreathing(SolidColorBrush brush)
    {
        var pulse = new ColorAnimation
        {
            From = Bright,
            To = Dim,
            Duration = BreathHalfCycle,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            // Ease at both ends so it reads as breathing rather than blinking.
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        brush.BeginAnimation(SolidColorBrush.ColorProperty, pulse);
    }

    public void Hide()
    {
        // Detach the animation before dropping the brush, so the clock stops ticking.
        _frameBrush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _frameBrush = null;

        foreach (var strip in _strips)
        {
            try
            {
                strip.Close();
            }
            catch
            {
                // Already gone (e.g. app shutting down).
            }
        }

        _strips.Clear();
    }

    private static Window CreateStrip(Rectangle boundsPx, MediaBrush fill)
    {
        var strip = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = fill,
            WindowStartupLocation = WindowStartupLocation.Manual,
            // Opaque, so no AllowsTransparency and therefore no software-rendering penalty.
            AllowsTransparency = false,
            Focusable = false,
        };

        strip.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(strip).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // Click-through and never focusable: the frame must not intercept clicks near the edge
            // of the recorded area, nor steal activation from whatever is being recorded.
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

            // Strip all non-client chrome. A WindowStyle.None window still keeps a caption/frame in
            // its window styles, and DWM draws a drop shadow for it — at the corners the overlapping
            // strips' shadows compound into a visible rounded dark-red blob that reads as a broken
            // border. Removing the frame styles (and disabling the DWM shadow below) leaves four
            // clean opaque rectangles with nothing but their fill.
            int style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, style & ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME));

            // Belt-and-braces: tell DWM not to render non-client content (the shadow) for this hwnd.
            int disabled = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref disabled, sizeof(int));

            // Moving a strip onto a monitor with a different DPI (e.g. 175% primary -> 100% external)
            // makes Windows send WM_DPICHANGED, and WPF's default handling resizes the window by the
            // DPI ratio — shrinking every strip to ~1/1.75 and breaking the frame. Swallow it and
            // re-assert the exact physical bounds instead; a solid fill doesn't care about DPI.
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_DPICHANGED)
                {
                    SetWindowPos(h, HWND_TOPMOST, boundsPx.X, boundsPx.Y, boundsPx.Width, boundsPx.Height, SWP_NOACTIVATE);
                    handled = true;
                }
                return IntPtr.Zero;
            });

            SetWindowPos(
                hwnd, HWND_TOPMOST,
                boundsPx.X, boundsPx.Y, boundsPx.Width, boundsPx.Height,
                SWP_NOACTIVATE | SWP_FRAMECHANGED);
        };

        strip.Show();
        return strip;
    }

    // --- Win32 ---

    private const int GWL_STYLE = -16;
    private const int WM_DPICHANGED = 0x02E0;
    private const int GWL_EXSTYLE = -20;

    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_CAPTION = WS_BORDER | WS_DLGFRAME;
    private const int WS_THICKFRAME = 0x00040000;

    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    // DWM: disable non-client rendering (the drop shadow) on the strip windows.
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int attrValue, int attrSize);
}
