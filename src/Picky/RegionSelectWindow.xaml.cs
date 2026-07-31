using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace Picky;

/// <summary>
/// Full-screen transparent overlay used to select a capture region. Two ways to grab:
/// hover a window and single-click to capture its auto-detected bounds (Snipping-Tool
/// window mode), or drag a freeform marquee.
/// </summary>
public partial class RegionSelectWindow : Window
{
    private Point _start;
    private bool _dragging;
    private bool _movedEnough;
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;
    private IntPtr _selfHandle;

    // Bounds (physical pixels) of the window currently under the cursor, for click-to-grab.
    private System.Drawing.Rectangle _candidatePx;

    private const double DragThreshold = 4.0;

    private readonly System.Windows.Media.ImageSource? _frozen;

    /// <param name="frozen">
    /// A snapshot of the whole screen taken before the overlay appeared, shown as the
    /// backdrop so transient UI (open menus, tooltips) can still be selected.
    /// </param>
    public RegionSelectWindow(System.Windows.Media.ImageSource? frozen = null)
    {
        InitializeComponent();

        _frozen = frozen;

        // Cover the whole virtual desktop explicitly. Relying on WindowState=Maximized
        // for a borderless AllowsTransparency window is unreliable (it can stay at the
        // default tiny size), so size to the virtual screen bounds instead.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // Selection box uses the app accent: solid stroke, translucent fill.
        SelectionRect.Stroke = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Brush.Accent"];
        var fill = AccentTheme.Current;
        fill.A = 0x33;
        SelectionRect.Fill = new SolidColorBrush(fill);

        Loaded += (_, _) =>
        {
            Dim.Width = ActualWidth;
            Dim.Height = ActualHeight;

            if (_frozen is not null)
            {
                Frozen.Source = _frozen;
                Frozen.Width = ActualWidth;
                Frozen.Height = ActualHeight;
            }

            _selfHandle = new WindowInteropHelper(this).Handle;
            var source = PresentationSource.FromVisual(this);
            _dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            _dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            Activate();
        };
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(RootCanvas);
        _dragging = true;
        _movedEnough = false;
        // Leave the current auto-detected highlight in place; a plain click grabs it.
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            UpdateCandidate();
            return;
        }

        var current = e.GetPosition(RootCanvas);

        // Below the threshold it's still a click (keep the auto-detected window highlight).
        if (!_movedEnough && (current - _start).Length < DragThreshold)
        {
            return;
        }

        _movedEnough = true;

        var x = Math.Min(_start.X, current.X);
        var y = Math.Min(_start.Y, current.Y);
        var w = Math.Abs(current.X - _start.X);
        var h = Math.Abs(current.Y - _start.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
        SelectionRect.Visibility = Visibility.Visible;
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        if (_movedEnough)
        {
            CommitManualSelection();
        }
        else
        {
            CommitAutoSelection();
        }
    }

    /// <summary>Freeform marquee → capture the drawn box.</summary>
    private void CommitManualSelection()
    {
        var left = Canvas.GetLeft(SelectionRect);
        var top = Canvas.GetTop(SelectionRect);
        var width = SelectionRect.Width;
        var height = SelectionRect.Height;

        if (width < 2 || height < 2)
        {
            DialogResult = false;
            Close();
            return;
        }

        // Convert device-independent WPF units to physical pixels for the capture.
        SelectedRegion = new System.Drawing.Rectangle(
            (int)((left + SystemParameters.VirtualScreenLeft) * _dpiX),
            (int)((top + SystemParameters.VirtualScreenTop) * _dpiY),
            (int)(width * _dpiX),
            (int)(height * _dpiY));

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Single click → capture the auto-detected window under the cursor, or, if nothing
    /// plausible was detected, the full screen the cursor is on.
    /// </summary>
    private void CommitAutoSelection()
    {
        if (_candidatePx.Width >= 2 && _candidatePx.Height >= 2)
        {
            SelectedRegion = _candidatePx;
        }
        else
        {
            var bounds = GetCursorPos(out var pt)
                ? System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y)).Bounds
                : System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            SelectedRegion = new System.Drawing.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        DialogResult = true;
        Close();
    }

    /// <summary>Finds the top-level window under the cursor and highlights its bounds.</summary>
    private void UpdateCandidate()
    {
        if (GetCursorPos(out var pt) && TryWindowUnderPoint(pt, out var rect))
        {
            var candidate = new System.Drawing.Rectangle(
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            candidate.Intersect(VirtualScreenPx());
            _candidatePx = candidate;

            if (candidate.Width >= 2 && candidate.Height >= 2)
            {
                // Physical px → canvas DIP (canvas origin sits at the virtual-screen origin).
                Canvas.SetLeft(SelectionRect, candidate.Left / _dpiX - SystemParameters.VirtualScreenLeft);
                Canvas.SetTop(SelectionRect, candidate.Top / _dpiY - SystemParameters.VirtualScreenTop);
                SelectionRect.Width = candidate.Width / _dpiX;
                SelectionRect.Height = candidate.Height / _dpiY;
                SelectionRect.Visibility = Visibility.Visible;
                return;
            }
        }

        _candidatePx = System.Drawing.Rectangle.Empty;
        SelectionRect.Visibility = Visibility.Collapsed;
    }

    private System.Drawing.Rectangle VirtualScreenPx() => new(
        (int)(SystemParameters.VirtualScreenLeft * _dpiX),
        (int)(SystemParameters.VirtualScreenTop * _dpiY),
        (int)(SystemParameters.VirtualScreenWidth * _dpiX),
        (int)(SystemParameters.VirtualScreenHeight * _dpiY));

    private bool TryWindowUnderPoint(POINT pt, out RECT rect)
    {
        IntPtr found = IntPtr.Zero;
        RECT foundRect = default;

        // EnumWindows walks top-of-Z-order first, so the first containing hit is the
        // frontmost real window under the cursor.
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == _selfHandle || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd))
            {
                return true;
            }

            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
            {
                return true; // skip palettes / tool windows
            }

            if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0)
            {
                return true;
            }

            if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
            {
                found = hwnd;
                foundRect = r;
                return false; // stop enumeration
            }

            return true;
        }, IntPtr.Zero);

        rect = foundRect;
        return found != IntPtr.Zero;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    public System.Drawing.Rectangle SelectedRegion { get; private set; }

    // --- Win32 window hit-testing ---

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;

    private static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

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
}
