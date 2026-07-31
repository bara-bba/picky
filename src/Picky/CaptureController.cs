using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

namespace Picky;

/// <summary>
/// Central capture flow shared by the main window and the tray icon:
/// grab pixels, auto-save a PNG into the chosen folder (Screenpresso-style),
/// then open the preview.
/// </summary>
internal static class CaptureController
{
    public static void CaptureRegion()
    {
        // Hide our own visible windows so they aren't baked into the frozen shot.
        var hidden = System.Windows.Application.Current.Windows
            .Cast<Window>()
            .Where(w => w.IsVisible)
            .ToList();
        foreach (var w in hidden)
        {
            w.Hide();
        }

        // Let the hides actually paint before we grab the screen.
        System.Windows.Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        System.Threading.Thread.Sleep(80);

        // Freeze the whole desktop first (so open menus / tooltips are preserved), then
        // let the user select a region on the still image instead of the live screen.
        var frozen = ScreenCapture.CaptureVirtualScreen(out var vsBounds);

        var overlay = new RegionSelectWindow(ToImageSource(frozen));
        var accepted = overlay.ShowDialog();

        // Restore whatever we hid — but only windows still open. A docked gallery may have
        // auto-closed itself while hidden, and Show() on a closed window throws.
        var stillOpen = System.Windows.Application.Current.Windows.Cast<Window>().ToHashSet();
        foreach (var w in hidden)
        {
            if (stillOpen.Contains(w))
            {
                w.Show();
            }
        }

        if (accepted == true)
        {
            var r = overlay.SelectedRegion;
            var crop = new System.Drawing.Rectangle(r.X - vsBounds.X, r.Y - vsBounds.Y, r.Width, r.Height);
            crop.Intersect(new System.Drawing.Rectangle(0, 0, frozen.Width, frozen.Height));

            if (crop.Width >= 1 && crop.Height >= 1)
            {
                using var cropped = frozen.Clone(crop, frozen.PixelFormat);
                ShowCapture(cropped);
            }
        }

        frozen.Dispose();
    }

    private static ImageSource ToImageSource(System.Drawing.Bitmap bitmap)
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

    public static void CaptureFullScreen(Window? owner)
    {
        bool reshow = owner is { IsVisible: true };
        if (reshow)
        {
            owner!.Hide();
            // Let the hide actually paint before we grab the screen, so our own
            // window isn't in the shot.
            owner!.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            System.Threading.Thread.Sleep(120);
        }

        var bounds = Screen.PrimaryScreen!.Bounds;
        using var bitmap = ScreenCapture.CaptureRegion(bounds);
        ShowCapture(bitmap);

        if (reshow)
        {
            owner!.Show();
        }
    }

    private static void ShowCapture(System.Drawing.Bitmap bitmap)
    {
        var savedPath = SaveCapture(bitmap);
        // Screenpresso-style: after saving, surface the gallery docked to the lower-right
        // corner (instead of a full preview) with the just-taken shot already selected.
        // Click a thumbnail there to open the full preview.
        ((App)System.Windows.Application.Current).ShowGalleryDocked(savedPath);
    }

    private static string SaveCapture(System.Drawing.Bitmap bitmap)
    {
        var folder = App.Settings.EnsureCaptureFolder();
        var path = Path.Combine(folder, $"Picky_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }
}
