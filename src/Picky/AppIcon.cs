using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;
using DColor = System.Drawing.Color;

namespace Picky;

/// <summary>
/// Draws the app glyph — an accent rounded square with a viewfinder (corner
/// brackets + focus dot) — at any size, and assembles a real multi-resolution
/// .ico. Vector-drawn per size so it stays crisp down to 16px.
/// </summary>
internal static class AppIcon
{
    private static readonly int[] IcoSizes = { 16, 24, 32, 48, 256 };

    public static Bitmap Render(int size, DColor accent, DColor glyph)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(DColor.Transparent);

        float s = size;
        float pad = s * 0.055f;
        float radius = s * 0.24f;

        using (var bg = new SolidBrush(accent))
        using (var square = RoundedRect(pad, pad, s - 2 * pad, s - 2 * pad, radius))
        {
            g.FillPath(bg, square);
        }

        float m = s * 0.30f;          // bracket corner inset
        float len = s * 0.14f;        // bracket arm length
        float thickness = Math.Max(1.4f, s * 0.075f);

        using var pen = new Pen(glyph, thickness)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        float r = s - m; // far edge
        g.DrawLines(pen, new[] { new PointF(m, m + len), new PointF(m, m), new PointF(m + len, m) });
        g.DrawLines(pen, new[] { new PointF(r - len, m), new PointF(r, m), new PointF(r, m + len) });
        g.DrawLines(pen, new[] { new PointF(m, r - len), new PointF(m, r), new PointF(m + len, r) });
        g.DrawLines(pen, new[] { new PointF(r - len, r), new PointF(r, r), new PointF(r, r - len) });

        // Focus dot in the center.
        float dot = s * 0.09f;
        using (var db = new SolidBrush(glyph))
        {
            g.FillEllipse(db, s / 2 - dot, s / 2 - dot, dot * 2, dot * 2);
        }

        return bmp;
    }

    /// <summary>System.Drawing.Icon with multiple frames — used for the tray.</summary>
    public static Icon CreateIcon(DColor accent, DColor glyph)
    {
        using var ms = new MemoryStream(BuildIco(accent, glyph));
        using var raw = new Icon(ms);
        return (Icon)raw.Clone();
    }

    /// <summary>WPF ImageSource for Window.Icon (title bar + taskbar).</summary>
    public static BitmapSource CreateImageSource(DColor accent, DColor glyph, int size = 64)
    {
        using var bmp = Render(size, accent, glyph);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Assembles a PNG-framed .ico (Vista+ format) with all standard sizes.</summary>
    public static byte[] BuildIco(DColor accent, DColor glyph)
    {
        var frames = new List<(int size, byte[] png)>();
        foreach (var size in IcoSizes)
        {
            using var bmp = Render(size, accent, glyph);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            frames.Add((size, ms.ToArray()));
        }

        using var outMs = new MemoryStream();
        using var w = new BinaryWriter(outMs);

        w.Write((short)0);              // reserved
        w.Write((short)1);              // type = icon
        w.Write((short)frames.Count);

        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 = 256)
            w.Write((byte)(size >= 256 ? 0 : size)); // height
            w.Write((byte)0);           // palette
            w.Write((byte)0);           // reserved
            w.Write((short)1);          // color planes
            w.Write((short)32);         // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in frames)
        {
            w.Write(png);
        }

        w.Flush();
        return outMs.ToArray();
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
