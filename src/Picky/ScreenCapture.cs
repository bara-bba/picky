using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Picky.Native;

namespace Picky;

/// <summary>
/// Raw pixel grabs. Every rectangle here is in <b>physical pixels</b> on the virtual
/// desktop (see <see cref="MonitorInfo"/>), which is the only coordinate space that is
/// unambiguous on a mixed-DPI multi-monitor setup.
/// </summary>
internal static class ScreenCapture
{
    /// <summary>
    /// Copies a rectangle of the virtual desktop.
    /// </summary>
    /// <param name="region">Region in physical pixels; the origin may be negative.</param>
    /// <param name="includeLayered">
    /// Adds <c>CAPTUREBLT</c>, which is what pulls in layered windows — open menus,
    /// tooltips, drop-shadows. Without it those are simply missing from the grab, which
    /// defeats the whole point of freeze-frame capture.
    /// </param>
    public static Bitmap CaptureRegion(Rectangle region, bool includeLayered = true)
    {
        if (region.Width < 1 || region.Height < 1)
        {
            throw new ArgumentException($"Capture region is empty: {region}", nameof(region));
        }

        // Format32bppRgb (not ...Argb): BitBlt never writes the alpha byte, so an Argb
        // surface would end up fully transparent and save as an invisible PNG.
        // Fully qualified: `using System.Windows.Media` also defines a PixelFormat.
        var bitmap = new Bitmap(region.Width, region.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);

        using var graphics = Graphics.FromImage(bitmap);

        // GetDC(NULL) is the DC for the entire virtual desktop, so negative source
        // coordinates (monitors left of / above the primary) blit correctly.
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            // Extremely unlikely; fall back to the managed path rather than fail the capture.
            graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }

        try
        {
            IntPtr targetDc = graphics.GetHdc();
            try
            {
                int flags = SRCCOPY | (includeLayered ? CAPTUREBLT : 0);
                BitBlt(targetDc, 0, 0, region.Width, region.Height, screenDc, region.Left, region.Top, flags);
            }
            finally
            {
                graphics.ReleaseHdc(targetDc);
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        return bitmap;
    }

    /// <summary>Grabs the entire virtual desktop (all monitors) and reports its pixel bounds.</summary>
    /// <remarks>
    /// The virtual desktop is a bounding box, so on a ragged layout (differing heights,
    /// a rotated panel) the areas no monitor covers come back black. That is unavoidable
    /// for a single flat image — use <see cref="CaptureMonitor"/> to avoid the padding.
    /// </remarks>
    public static Bitmap CaptureVirtualScreen(out Rectangle bounds)
    {
        bounds = MonitorInfo.VirtualScreen;
        return CaptureRegion(bounds);
    }

    /// <summary>Grabs exactly one display, with no dead padding.</summary>
    public static Bitmap CaptureMonitor(MonitorInfo.Monitor monitor, out Rectangle bounds)
    {
        bounds = monitor.Bounds;
        return CaptureRegion(bounds);
    }

    /// <summary>Grabs the display the mouse pointer is on.</summary>
    public static Bitmap CaptureMonitorUnderCursor(out Rectangle bounds)
        => CaptureMonitor(MonitorInfo.FromCursor(), out bounds);

    /// <summary>
    /// Hands a GDI bitmap to WPF as a frozen, cross-thread-safe <see cref="ImageSource"/>.
    /// </summary>
    public static ImageSource ToImageSource(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // --- Win32 ---

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);
}
