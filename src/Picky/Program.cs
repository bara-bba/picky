using System.IO;
using Velopack;

namespace Picky;

/// <summary>
/// Explicit entry point, replacing the one WPF generates from <c>App.xaml</c>
/// (selected via <c>&lt;StartupObject&gt;</c> in the csproj).
///
/// <para>Velopack has to run before any window exists. On the first launch after an install or an
/// update it does housekeeping — creating shortcuts, finalising a staged update — and in some of
/// those hooks it exits the process immediately. If the WPF application had already started, the
/// user would see the tray icon or a window flash up and vanish.</para>
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // No-ops when the app isn't running from a Velopack install (e.g. straight out of bin/),
        // so day-to-day debugging is unaffected.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => CleanUpUserData())
            .Run();

        var app = new App();

        // Required with a hand-written Main: this is what loads App.xaml's merged resource
        // dictionaries (Theme.xaml). Without it every StaticResource Brush.* lookup fails at
        // window-load time.
        app.InitializeComponent();

        app.Run();
    }

    /// <summary>
    /// Runs during uninstall. Velopack removes its own install tree (%LocalAppData%\Picky, which
    /// includes errors.log and the thumbnail cache), but never touches the roaming config folder —
    /// so we delete it here to leave nothing behind.
    ///
    /// <para>Captured screenshots and recordings live in Pictures\Picky, NOT in AppData, and are
    /// deliberately preserved: an uninstall must never destroy the user's own files.</para>
    /// </summary>
    private static void CleanUpUserData()
    {
        try
        {
            var roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Picky");
            if (Directory.Exists(roaming))
            {
                Directory.Delete(roaming, recursive: true);
            }
        }
        catch
        {
            // Uninstall must never fail because leftover-data cleanup hit a locked file.
        }
    }
}
