using System.IO;
using System.Threading;
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
    // Held for the whole process lifetime so the OS keeps the single-instance mutex owned.
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        // No-ops when the app isn't running from a Velopack install (e.g. straight out of bin/),
        // so day-to-day debugging is unaffected.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => CleanUpUserData())
            .Run();

        // Single-instance guard — stops a second Picky.exe from running (e.g. an update restart
        // overlapping the old process, or "Start with Windows" coinciding with a manual launch).
        //
        // Placed AFTER VelopackApp.Run() so short-lived install/update/uninstall hooks, which exit
        // inside Run(), are never blocked. Velopack's ApplyUpdatesAndRestart exits the old process
        // (via Environment.Exit, which *abandons* the mutex) before launching the new build, so:
        //   - AbandonedMutexException means the previous owner is gone and we now own it — proceed.
        //   - the brief WaitOne timeout absorbs any handoff overlap without falsely blocking a restart.
        _singleInstance = new Mutex(initiallyOwned: false, @"Local\Picky.SingleInstance");
        bool owned;
        try
        {
            owned = _singleInstance.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            return; // another Picky is already running
        }

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
