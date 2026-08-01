using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Picky.Native;

namespace Picky;

/// <summary>
/// Central capture flow shared by the control panel and the tray icon: grab pixels,
/// auto-save a PNG into the chosen folder (Screenpresso-style), then pop the gallery.
///
/// All geometry is in physical pixels on the virtual desktop, so every path here behaves
/// identically regardless of how many monitors there are or how they're scaled.
/// </summary>
internal static class CaptureController
{
    /// <summary>Freeze the desktop, let the user pick a region on the still image, crop it.</summary>
    public static void CaptureRegion()
    {
        var hidden = HideOwnWindows();

        // Freeze the whole desktop first (so open menus / tooltips are preserved), then
        // select on the still image instead of the live screen.
        using var frozen = ScreenCapture.CaptureVirtualScreen(out var frozenBounds);

        bool accepted;
        Rectangle selected;
        try
        {
            var overlay = new RegionSelectWindow(ScreenCapture.ToImageSource(frozen), frozenBounds);
            accepted = overlay.ShowDialog() == true;
            selected = overlay.SelectedRegion;
        }
        finally
        {
            RestoreOwnWindows(hidden);
        }

        if (!accepted)
        {
            return;
        }

        // Virtual-desktop pixels -> offsets inside the frozen bitmap.
        var crop = selected;
        crop.Offset(-frozenBounds.X, -frozenBounds.Y);
        crop.Intersect(new Rectangle(0, 0, frozen.Width, frozen.Height));

        if (crop.Width < 1 || crop.Height < 1)
        {
            return;
        }

        using var cropped = frozen.Clone(crop, frozen.PixelFormat);
        ShowCapture(cropped);
    }

    /// <summary>Grabs the entire monitor the mouse pointer is currently on.</summary>
    public static void CaptureCurrentScreen() => CaptureScreen(MonitorInfo.FromCursor());

    /// <summary>Grabs one specific monitor, edge to edge, with no dead padding.</summary>
    public static void CaptureScreen(MonitorInfo.Monitor monitor)
    {
        var hidden = HideOwnWindows();

        Bitmap bitmap;
        try
        {
            bitmap = ScreenCapture.CaptureMonitor(monitor, out _);
        }
        finally
        {
            RestoreOwnWindows(hidden);
        }

        using (bitmap)
        {
            ShowCapture(bitmap);
        }
    }

    /// <summary>
    /// Grabs every monitor as one flat image spanning the whole virtual desktop.
    /// </summary>
    /// <remarks>
    /// The virtual desktop is a bounding box, so on a ragged layout the regions no monitor
    /// covers come out black. That's inherent to a single rectangular image — use
    /// <see cref="CaptureScreen"/> per display to avoid it.
    /// </remarks>
    public static void CaptureAllScreens()
    {
        var hidden = HideOwnWindows();

        Bitmap bitmap;
        try
        {
            bitmap = ScreenCapture.CaptureVirtualScreen(out _);
        }
        finally
        {
            RestoreOwnWindows(hidden);
        }

        using (bitmap)
        {
            ShowCapture(bitmap);
        }
    }

    /// <summary>
    /// Hides every visible Picky window so none of them is baked into the shot, and waits
    /// for the hide to actually reach the screen.
    /// </summary>
    private static List<Window> HideOwnWindows()
    {
        var app = System.Windows.Application.Current;

        var hidden = app.Windows
            .Cast<Window>()
            .Where(w => w.IsVisible)
            .ToList();

        foreach (var window in hidden)
        {
            window.Hide();
        }

        // Let the hides paint before we grab pixels.
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        System.Threading.Thread.Sleep(120);

        return hidden;
    }

    private static void RestoreOwnWindows(List<Window> hidden)
    {
        // Only windows still open. A docked gallery may have auto-closed itself while
        // hidden, and Show() on a closed window throws.
        var stillOpen = System.Windows.Application.Current.Windows.Cast<Window>().ToHashSet();

        foreach (var window in hidden)
        {
            if (stillOpen.Contains(window))
            {
                window.Show();
            }
        }
    }

    private static void ShowCapture(Bitmap bitmap)
    {
        var savedPath = SaveCapture(bitmap);

        // Screenpresso-style: after saving, surface the gallery docked to the lower-right
        // corner of the active monitor with the just-taken shot already selected.
        ((App)System.Windows.Application.Current).ShowGalleryDocked(savedPath);
    }

    private static string SaveCapture(Bitmap bitmap)
    {
        var folder = App.Settings.EnsureCaptureFolder();
        var path = Path.Combine(folder, $"Picky_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }
}
