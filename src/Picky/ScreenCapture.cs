using System.Drawing;
using System.Drawing.Imaging;

namespace Picky;

internal static class ScreenCapture
{
    public static Bitmap CaptureRegion(Rectangle region)
    {
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>Grabs the entire virtual desktop (all monitors) and reports its pixel bounds.</summary>
    public static Bitmap CaptureVirtualScreen(out Rectangle bounds)
    {
        bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        return CaptureRegion(bounds);
    }
}
