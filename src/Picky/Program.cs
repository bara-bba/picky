using System.Diagnostics;
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

        if (!TryBecomeSingleInstance())
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
    /// Ensures exactly one Picky runs. Returns false if another instance already owns the slot
    /// (this process should then exit).
    ///
    /// <para>Two layers, because the two ways a second process appears need different handling:</para>
    /// <list type="bullet">
    /// <item>A named mutex stops a second <em>guard-aware</em> instance (a manual double-launch, or
    /// "Start with Windows" racing a manual open): the loser can't take the mutex and bails.</item>
    /// <item>Winning the mutex, we then kill any other <c>Picky.exe</c> still running. That covers the
    /// update case a mutex can't: a new build restarts while the <em>old</em> build (which had no
    /// guard, so never took the mutex) is still alive — the mutex is free, so we'd otherwise run
    /// alongside it. Killing strays collapses that to one.</item>
    /// </list>
    ///
    /// <para>Placed after <c>VelopackApp.Run()</c> so short-lived install/update/uninstall hooks
    /// (which exit inside Run()) are never blocked. Velopack exits the old process via
    /// <c>Environment.Exit</c>, which <em>abandons</em> the mutex — caught below as ownership.</para>
    /// </summary>
    private static bool TryBecomeSingleInstance()
    {
        _singleInstance = new Mutex(initiallyOwned: false, @"Local\Picky.SingleInstance");

        bool owned;
        try
        {
            owned = _singleInstance.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            owned = true; // previous owner exited abnormally; ownership passes to us
        }

        if (!owned)
        {
            return false;
        }

        TerminateOtherInstances();
        return true;
    }

    /// <summary>Kills every other Picky.exe. Safe because we hold the single-instance mutex, so any
    /// other Picky process is by definition a stray (an un-exited old build).</summary>
    private static void TerminateOtherInstances()
    {
        try
        {
            var myId = Environment.ProcessId;
            foreach (var other in Process.GetProcessesByName("Picky"))
            {
                using (other)
                {
                    if (other.Id == myId)
                    {
                        continue;
                    }
                    try
                    {
                        other.Kill();
                        other.WaitForExit(2000);
                    }
                    catch
                    {
                        // Already exiting, or we can't touch it (different session/elevation) — ignore.
                    }
                }
            }
        }
        catch
        {
            // Enumeration failed — never block startup over stray cleanup.
        }
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
