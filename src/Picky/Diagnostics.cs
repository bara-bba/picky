using Picky.Native;

namespace Picky;

/// <summary>
/// Self-diagnosis for capture problems, invoked with <c>Picky.exe --probe &lt;folder&gt;</c>.
///
/// Writes the detected display layout, a capture of the whole virtual desktop and of each
/// display, and a window-to-display map — all in true physical pixels. Reporting the mean
/// brightness of each grab next to the expected geometry makes the two very different
/// failure modes easy to tell apart: wrong <i>size/offset</i> (a coordinate bug) versus a
/// correctly-sized but <i>black</i> image (nothing was being rendered, e.g. a sleeping display).
/// </summary>
internal static class Diagnostics
{
    public static void RunProbe(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);

        var monitors = MonitorInfo.All();
        var report = new List<string>
        {
            $"VirtualScreen (physical px) : {MonitorInfo.VirtualScreen}",
            $"Displays detected           : {monitors.Count}",
            "",
        };

        foreach (var monitor in monitors)
        {
            report.Add(monitor.Label);
            report.Add($"    bounds   = {monitor.Bounds}");
            report.Add($"    workArea = {monitor.WorkArea}");
            report.Add($"    device   = {monitor.DeviceName}");
        }

        report.Add("");

        using (var all = ScreenCapture.CaptureVirtualScreen(out var virtualBounds))
        {
            all.Save(System.IO.Path.Combine(folder, "virtual-screen.png"), System.Drawing.Imaging.ImageFormat.Png);
            report.Add($"virtual-screen.png : {all.Width}x{all.Height} (expected {virtualBounds.Width}x{virtualBounds.Height})  meanBrightness={MeanBrightness(all):N1}");
        }

        foreach (var monitor in monitors)
        {
            using var shot = ScreenCapture.CaptureMonitor(monitor, out var bounds);
            var fileName = $"display-{monitor.Index}.png";
            shot.Save(System.IO.Path.Combine(folder, fileName), System.Drawing.Imaging.ImageFormat.Png);
            report.Add($"{fileName} : {shot.Width}x{shot.Height} (expected {bounds.Width}x{bounds.Height})  meanBrightness={MeanBrightness(shot):N1}");
        }

        report.Add("");
        report.Add("On-screen windows — 'visible' is what click-to-grab captures;");
        report.Add("'outer' is GetWindowRect including the invisible resize border:");

        foreach (var (visible, outer, title) in OnScreenWindows())
        {
            var centre = new System.Drawing.Point(visible.X + visible.Width / 2, visible.Y + visible.Height / 2);
            var host = monitors.FirstOrDefault(m => m.Bounds.Contains(centre));

            report.Add($"    \"{title}\"");
            report.Add($"        visible = {visible.Width}x{visible.Height} @ {visible.X},{visible.Y}   -> {(host is null ? "off-desktop" : "Display " + host.Index)}");
            report.Add($"        outer   = {outer.Width}x{outer.Height} @ {outer.X},{outer.Y}   (border inset L{visible.Left - outer.Left} T{visible.Top - outer.Top} R{outer.Right - visible.Right} B{outer.Bottom - visible.Bottom})");
        }

        System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "monitors.txt"), report);
    }

    /// <summary>Average luminance over a sparse sample; 0 means a completely black grab.</summary>
    private static double MeanBrightness(Bitmap bitmap)
    {
        double sum = 0;
        int count = 0;
        int stepX = Math.Max(1, bitmap.Width / 60);
        int stepY = Math.Max(1, bitmap.Height / 60);

        for (int x = 0; x < bitmap.Width; x += stepX)
        {
            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                var pixel = bitmap.GetPixel(x, y);
                sum += (pixel.R + pixel.G + pixel.B) / 3.0;
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// Windows genuinely visible right now, reported through the same <see cref="WindowInfo"/>
    /// helpers the capture overlay uses — so the probe reflects real capture geometry rather than
    /// a re-implementation that could drift from it.
    /// </summary>
    private static List<(Rectangle Visible, Rectangle Outer, string Title)> OnScreenWindows()
    {
        var found = new List<(Rectangle, Rectangle, string)>();

        WindowInfo.ForEachTopLevel(hwnd =>
        {
            if (!WindowInfo.IsVisible(hwnd) || WindowInfo.IsMinimised(hwnd) || WindowInfo.IsCloaked(hwnd))
            {
                return true;
            }

            if (!WindowInfo.TryVisibleBounds(hwnd, out var visible)
                || visible.Width < 250 || visible.Height < 150)
            {
                return true;
            }

            var title = WindowInfo.GetTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            WindowInfo.TryOuterBounds(hwnd, out var outer);
            found.Add((visible, outer, title));
            return true;
        });

        return found;
    }
}
