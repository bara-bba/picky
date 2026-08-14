using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Picky.Native;
using Point = System.Windows.Point;
using DrawingPoint = System.Drawing.Point;

namespace Picky;

/// <summary>
/// Full-desktop overlay used to select a capture region. Two ways to grab: hover a window
/// and single-click to capture its auto-detected bounds (Snipping-Tool window mode), or
/// drag a freeform marquee.
///
/// <para><b>Multi-monitor / mixed-DPI correctness.</b> The overlay is sized and positioned
/// with <c>SetWindowPos</c> in raw physical pixels rather than through WPF's
/// <c>Left/Top/Width/Height</c>. WPF expresses those in device-independent units scaled by
/// a *single* monitor's DPI, so on a desktop mixing (say) 175% and 100% displays the window
/// silently lands in the wrong place at the wrong size.</para>
///
/// <para>Selection coordinates are then converted using the ratio between the frozen
/// bitmap's pixel size and the canvas's rendered size. Because the backdrop image is
/// stretched to fill the window exactly, that ratio is correct by construction for every
/// monitor at once — there is deliberately no per-monitor DPI scalar anywhere in this
/// file.</para>
/// </summary>
public partial class RegionSelectWindow : Window
{
    /// <summary>Physical-pixel rect that the overlay covers; identical to the frozen bitmap's bounds.</summary>
    private readonly Rectangle _canvasPx;

    private readonly ImageSource _frozen;

    /// <summary>The frozen backdrop as a bitmap, so the loupe can crop pixels out of it.</summary>
    private readonly BitmapSource? _frozenBitmap;

    /// <summary>Source pixels shown across the loupe. Odd, so there is a true centre pixel.</summary>
    private const int LensPixels = 15;

    /// <summary>DIPs per magnified pixel. Must match the loupe sizes in the XAML (15 * 9 = 135).</summary>
    private const int LensCell = 9;

    private Point _start;
    private bool _dragging;
    private bool _movedEnough;
    private IntPtr _selfHandle;

    /// <summary>Global keyboard hook that makes Esc work without OS keyboard focus.</summary>
    private IntPtr _escHook = IntPtr.Zero;

    /// <summary>Held so the delegate handed to Win32 isn't collected while the hook is live.</summary>
    private LowLevelKeyboardProc? _escProc;

    /// <summary>Set once cancellation is under way, so it can only happen once.</summary>
    private bool _closing;

    /// <summary>Bounds (physical px) of the window currently under the cursor, for click-to-grab.</summary>
    private Rectangle _candidatePx;

    private const double DragThreshold = 4.0;

    /// <param name="frozen">
    /// Snapshot of the whole virtual desktop taken before the overlay appeared, shown as the
    /// backdrop so transient UI (open menus, tooltips) can still be selected. When omitted
    /// the overlay grabs its own snapshot, so the backdrop is never blank.
    /// </param>
    /// <param name="frozenBoundsPx">Physical-pixel bounds that <paramref name="frozen"/> covers.</param>
    public RegionSelectWindow(ImageSource? frozen = null, Rectangle? frozenBoundsPx = null)
    {
        InitializeComponent();

        if (frozen is not null)
        {
            _frozen = frozen;
            _canvasPx = frozenBoundsPx ?? MonitorInfo.VirtualScreen;
        }
        else
        {
            // Self-freeze. The window is opaque now (no AllowsTransparency), so a backdrop
            // is mandatory rather than optional.
            using var own = ScreenCapture.CaptureVirtualScreen(out var bounds);
            _frozen = ScreenCapture.ToImageSource(own);
            _canvasPx = bounds;
        }

        // Selection box uses the app accent: solid stroke, translucent fill.
        SelectionRect.Stroke = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Brush.Accent"];
        var fill = AccentTheme.Current;
        fill.A = 0x22;
        SelectionRect.Fill = new SolidColorBrush(fill);

        Frozen.Source = _frozen;
        Bright.Source = _frozen;
        Bright.Clip = Geometry.Empty; // nothing un-dimmed until there is a selection

        // The loupe crops straight out of the frozen backdrop, so what it magnifies is exactly
        // what will be captured.
        _frozenBitmap = _frozen as BitmapSource;
        if (_frozenBitmap is not null)
        {
            LensImage.Source = _frozenBitmap;
        }

        Loaded += (_, _) =>
        {
            ApplyPhysicalBounds(); // re-assert in case WPF re-applied its own layout
            PositionHint();

            // WPF's Activate()/Focus() alone aren't reliably enough here: ApplyPhysicalBounds
            // uses SWP_NOACTIVATE (deliberately, so repositioning never steals focus mid-drag),
            // and this window is shown right after CaptureController hides every other Picky
            // window, so there's no guarantee Windows hands keyboard focus to it without an
            // explicit foreground request. Without this, Esc silently does nothing until the
            // user clicks or moves the mouse over the overlay first.
            SetForegroundWindow(_selfHandle);
            Activate();
            Focus();

            // Show the loupe straight away rather than waiting for the first mouse move.
            UpdateCandidate();
            UpdateLens(PxToCanvas(MonitorInfo.CursorPosition.X, MonitorInfo.CursorPosition.Y));
        };
    }

    /// <summary>
    /// Sizes the HWND to the exact physical pixel rect it must cover, bypassing WPF's
    /// DIP conversion (which cannot represent a mixed-DPI span).
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _selfHandle = new WindowInteropHelper(this).Handle;
        ApplyPhysicalBounds();
        InstallEscapeHook();
    }

    /// <summary>Tears the hook down deterministically, however the overlay was dismissed.</summary>
    protected override void OnClosed(EventArgs e)
    {
        RemoveEscapeHook();
        base.OnClosed(e);
    }

    private void ApplyPhysicalBounds()
    {
        if (_selfHandle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            _selfHandle, IntPtr.Zero,
            _canvasPx.X, _canvasPx.Y, _canvasPx.Width, _canvasPx.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // --- Coordinate mapping (canvas DIPs <-> physical pixels) ---

    private double PxPerDipX => RootCanvas.ActualWidth > 0 ? _canvasPx.Width / RootCanvas.ActualWidth : 1.0;

    private double PxPerDipY => RootCanvas.ActualHeight > 0 ? _canvasPx.Height / RootCanvas.ActualHeight : 1.0;

    private DrawingPoint CanvasToPx(Point p) => new(
        _canvasPx.X + (int)Math.Round(p.X * PxPerDipX),
        _canvasPx.Y + (int)Math.Round(p.Y * PxPerDipY));

    private Point PxToCanvas(double x, double y) => new(
        (x - _canvasPx.X) / PxPerDipX,
        (y - _canvasPx.Y) / PxPerDipY);

    // --- Mouse ---

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _start = e.GetPosition(RootCanvas);
        _dragging = true;
        _movedEnough = false;
        // Leave the current auto-detected highlight in place; a plain click grabs it.
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(RootCanvas);

        if (!_dragging)
        {
            UpdateCandidate();
            UpdateLens(position); // after UpdateCandidate, so the label can show its size
            return;
        }

        var current = position;

        // Below the threshold it's still a click (keep the auto-detected window highlight).
        if (!_movedEnough && (current - _start).Length < DragThreshold)
        {
            return;
        }

        _movedEnough = true;

        ShowSelection(
            Math.Min(_start.X, current.X),
            Math.Min(_start.Y, current.Y),
            Math.Abs(current.X - _start.X),
            Math.Abs(current.Y - _start.Y));

        UpdateLens(current);
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || e.ChangedButton != MouseButton.Left)
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

    /// <summary>Right-click aborts, same as Esc.</summary>
    private void Canvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => Cancel();

    /// <summary>Draws the selection outline, un-dims the chosen area, and updates the readout.</summary>
    private void ShowSelection(double x, double y, double width, double height)
    {
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = width;
        SelectionRect.Height = height;
        SelectionRect.Visibility = Visibility.Visible;

        // Cut-out: the same screenshot, clipped to the selection, drawn over the dim layer.
        Bright.Clip = new RectangleGeometry(new Rect(x, y, width, height));
    }

    private void ClearSelection()
    {
        SelectionRect.Visibility = Visibility.Collapsed;
        Bright.Clip = Geometry.Empty;
    }

    /// <summary>
    /// Magnifies the frozen pixels around <paramref name="canvas"/> into the loupe and parks it
    /// clear of the cursor.
    /// </summary>
    private void UpdateLens(Point canvas)
    {
        if (_frozenBitmap is null)
        {
            return;
        }

        // Bitmap indices: the frozen image starts at the virtual-desktop origin, which can be negative.
        var cursorPx = CanvasToPx(canvas);
        int bx = cursorPx.X - _canvasPx.X;
        int by = cursorPx.Y - _canvasPx.Y;

        int width = _frozenBitmap.PixelWidth;
        int height = _frozenBitmap.PixelHeight;

        if (width < LensPixels || height < LensPixels)
        {
            return;
        }

        int half = LensPixels / 2;
        int wantLeft = bx - half;
        int wantTop = by - half;

        // Clamp the crop into the bitmap, then shift the image back by however much we clamped, so
        // the pixel under the cursor stays under the crosshair even at the edges of the desktop.
        int left = Math.Clamp(wantLeft, 0, width - LensPixels);
        int top = Math.Clamp(wantTop, 0, height - LensPixels);

        LensImage.Source = new CroppedBitmap(_frozenBitmap, new Int32Rect(left, top, LensPixels, LensPixels));
        LensOffset.X = (left - wantLeft) * LensCell;
        LensOffset.Y = (top - wantTop) * LensCell;

        LensText.Text = SelectionRect.Visibility == Visibility.Visible
            ? $"{PxSize().Width} × {PxSize().Height}"
            : $"{cursorPx.X}, {cursorPx.Y}";

        LensPanel.Visibility = Visibility.Visible;
        PositionLens(canvas);
    }

    /// <summary>Current selection size in physical pixels.</summary>
    private System.Drawing.Size PxSize()
    {
        var topLeft = CanvasToPx(new Point(Canvas.GetLeft(SelectionRect), Canvas.GetTop(SelectionRect)));
        var bottomRight = CanvasToPx(new Point(
            Canvas.GetLeft(SelectionRect) + SelectionRect.Width,
            Canvas.GetTop(SelectionRect) + SelectionRect.Height));

        return new System.Drawing.Size(bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    /// <summary>
    /// Keeps the loupe next to the cursor without sitting on top of it, flipping side or vertical
    /// position rather than sliding off the display.
    /// </summary>
    /// <remarks>
    /// Bounds come from the monitor under the cursor, never from <c>RootCanvas</c>. The canvas spans
    /// the whole virtual desktop, which is a bounding box: with displays of different heights or
    /// offsets, large parts of it belong to no monitor. Testing the flip against the canvas meant
    /// that near the bottom of a shorter or vertically-offset display there was still "room" below
    /// in canvas terms, so the loupe never flipped up and was drawn into that dead area — visible
    /// nowhere. Clamping per-monitor keeps it on the screen the user is actually looking at.
    /// </remarks>
    private void PositionLens(Point canvas)
    {
        const double gap = 22;

        LensPanel.UpdateLayout();
        double w = LensPanel.ActualWidth;
        double h = LensPanel.ActualHeight;

        var monitor = MonitorInfo.FromPoint(CanvasToPx(canvas)).Bounds;
        var min = PxToCanvas(monitor.Left, monitor.Top);
        var max = PxToCanvas(monitor.Right, monitor.Bottom);

        double x = canvas.X + gap;
        if (x + w > max.X)
        {
            x = canvas.X - gap - w;
        }

        double y = canvas.Y + gap;
        if (y + h > max.Y)
        {
            y = canvas.Y - gap - h;
        }

        Canvas.SetLeft(LensPanel, Math.Clamp(x, min.X, Math.Max(min.X, max.X - w)));
        Canvas.SetTop(LensPanel, Math.Clamp(y, min.Y, Math.Max(min.Y, max.Y - h)));
    }

    /// <summary>Puts the hint on the monitor the pointer is on, not always the leftmost one.</summary>
    private void PositionHint()
    {
        var monitor = MonitorInfo.FromCursor();
        var topLeft = PxToCanvas(monitor.Bounds.Left, monitor.Bounds.Top);
        var bottomRight = PxToCanvas(monitor.Bounds.Right, monitor.Bounds.Bottom);

        HintPanel.UpdateLayout();

        double centered = topLeft.X + ((bottomRight.X - topLeft.X) - HintPanel.ActualWidth) / 2;
        Canvas.SetLeft(HintPanel, Math.Max(topLeft.X + 8, centered));
        Canvas.SetTop(HintPanel, topLeft.Y + 32);
    }

    // --- Commit ---

    /// <summary>Freeform marquee → capture the drawn box.</summary>
    private void CommitManualSelection()
    {
        double left = Canvas.GetLeft(SelectionRect);
        double top = Canvas.GetTop(SelectionRect);
        double width = SelectionRect.Width;
        double height = SelectionRect.Height;

        if (width < 2 || height < 2)
        {
            Cancel();
            return;
        }

        // Map both corners through the same transform so width/height can't drift by a
        // rounding step, then clamp to what was actually captured.
        var topLeft = CanvasToPx(new Point(left, top));
        var bottomRight = CanvasToPx(new Point(left + width, top + height));

        var region = Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        region.Intersect(_canvasPx);

        if (region.Width < 1 || region.Height < 1)
        {
            Cancel();
            return;
        }

        SelectedRegion = region;
        DialogResult = true;
    }

    /// <summary>
    /// Single click → capture the auto-detected window under the cursor, or, if nothing
    /// plausible was detected, the whole monitor the cursor is on.
    /// </summary>
    private void CommitAutoSelection()
    {
        var region = _candidatePx.Width >= 2 && _candidatePx.Height >= 2
            ? _candidatePx
            : MonitorInfo.FromCursor().Bounds;

        region.Intersect(_canvasPx);

        if (region.Width < 1 || region.Height < 1)
        {
            Cancel();
            return;
        }

        SelectedRegion = region;
        DialogResult = true;
    }

    /// <summary>
    /// Aborts the pick. Guarded because it can now arrive from three places (Esc via WPF focus,
    /// Esc via the global hook, right-click) and setting <see cref="Window.DialogResult"/> on an
    /// already-closed window throws.
    /// </summary>
    private void Cancel()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        DialogResult = false;
    }

    /// <summary>Finds the top-level window under the cursor and highlights its bounds.</summary>
    private void UpdateCandidate()
    {
        var cursor = MonitorInfo.CursorPosition;

        if (TryWindowUnderPoint(cursor, out var candidate))
        {
            candidate.Intersect(_canvasPx);
            _candidatePx = candidate;

            if (candidate.Width >= 2 && candidate.Height >= 2)
            {
                var topLeft = PxToCanvas(candidate.Left, candidate.Top);
                var bottomRight = PxToCanvas(candidate.Right, candidate.Bottom);
                ShowSelection(
                    topLeft.X,
                    topLeft.Y,
                    bottomRight.X - topLeft.X,
                    bottomRight.Y - topLeft.Y);
                return;
            }
        }

        _candidatePx = Rectangle.Empty;
        ClearSelection();
    }

    private bool TryWindowUnderPoint(DrawingPoint pt, out Rectangle bounds)
    {
        IntPtr found = IntPtr.Zero;
        Rectangle foundBounds = Rectangle.Empty;

        // Walks top-of-Z-order first, so the first containing hit is the frontmost real window.
        WindowInfo.ForEachTopLevel(hwnd =>
        {
            if (hwnd == _selfHandle
                || !WindowInfo.IsVisible(hwnd)
                || WindowInfo.IsMinimised(hwnd)
                || WindowInfo.IsCloaked(hwnd)
                || WindowInfo.IsToolWindow(hwnd))
            {
                return true;
            }

            // Visible bounds, not GetWindowRect: the latter includes the invisible resize border,
            // which made auto-detect grab a rectangle wider and taller than the window.
            if (!WindowInfo.TryVisibleBounds(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                return true;
            }

            if (rect.Contains(pt.X, pt.Y))
            {
                found = hwnd;
                foundBounds = rect;
                return false; // stop enumeration
            }

            return true;
        });

        bounds = foundBounds;
        return found != IntPtr.Zero;
    }

    /// <summary>
    /// Fast path: works whenever the overlay actually holds WPF keyboard focus.
    /// <see cref="EscapeHookCallback"/> is the safety net for when it doesn't.
    /// </summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
        }
    }

    /// <summary>
    /// Watches Esc system-wide for as long as the overlay is up.
    /// </summary>
    /// <remarks>
    /// <para>Keyboard focus cannot be relied on here. The overlay is shown from a tray-resident
    /// process, and <c>SetForegroundWindow</c> is refused by Windows' foreground lock unless the
    /// caller qualifies — which it frequently does not: when the hotkey arrives through
    /// <see cref="HotKeyService"/>'s low-level hook (PrtScn) the keystroke was delivered to the app
    /// underneath, not to us; the tray menu path defers 180 ms and hands foreground back to the
    /// previous app; the gallery toolbar path hides the gallery first, which does the same; and a
    /// fullscreen foreground app holds the lock outright.</para>
    /// <para>The overlay is <c>Topmost</c>, so it is always visible and always receives clicks even
    /// when it has no focus — which is exactly why the failure looked intermittent: the mouse worked,
    /// Esc did nothing, and a single click "fixed" it by finally granting focus. Hooking the key
    /// removes focus from the equation entirely.</para>
    /// </remarks>
    private IntPtr EscapeHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_closing)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field
                if (vk == VK_ESCAPE)
                {
                    // Queued rather than run inline: closing the window would tear down the
                    // nested ShowDialog message pump this callback was invoked from.
                    Dispatcher.BeginInvoke(new Action(Cancel));

                    // Swallow it. While the overlay owns the screen, Esc belongs to the overlay
                    // and must not also reach whatever is behind it.
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_escHook, nCode, wParam, lParam);
    }

    private void InstallEscapeHook()
    {
        if (_escHook != IntPtr.Zero)
        {
            return;
        }

        _escProc = EscapeHookCallback;
        _escHook = SetWindowsHookEx(WH_KEYBOARD_LL, _escProc, GetModuleHandle(null), 0);
    }

    private void RemoveEscapeHook()
    {
        if (_escHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_escHook);
            _escHook = IntPtr.Zero;
        }

        _escProc = null;
    }

    /// <summary>The chosen region in physical pixels on the virtual desktop.</summary>
    public Rectangle SelectedRegion { get; private set; }

    // --- Win32 ---

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_ESCAPE = 0x1B;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
