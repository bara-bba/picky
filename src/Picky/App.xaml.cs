using System.Windows;
using Picky.Native;
using Velopack;
using Velopack.Sources;
using WinForms = System.Windows.Forms;

namespace Picky;

public partial class App : Application
{
    /// <summary>Update feed. Velopack reads the assets attached to GitHub Releases here.</summary>
    private const string RepositoryUrl = "https://github.com/bara-bba/picky";

    private WinForms.NotifyIcon _trayIcon = null!;
    private MainWindow _mainWindow = null!;
    private GalleryWindow? _gallery;
    private HotKeyService _hotKeys = null!;

    private readonly RecordingController _recorder = new();
    private readonly RecordingBorder _recordBorder = new();
    private WinForms.ToolStripMenuItem _recordMenuItem = null!;
    private WinForms.ToolStripMenuItem _captureScreenMenu = null!;

    // Staged auto-update, if one has been downloaded and is waiting for a restart.
    private WinForms.ToolStripMenuItem _updateMenuItem = null!;
    private UpdateManager? _updateManager;
    private UpdateInfo? _pendingUpdate;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private Window? _recordBar;
    private System.Windows.Threading.DispatcherTimer? _recordTimer;
    private DateTime _recordStart;

    internal static AppSettings Settings { get; private set; } = null!;

    /// <summary>The hotkey currently registered (may differ from the saved preference on fallback).</summary>
    internal HotKeyDef CurrentHotKey { get; private set; } = HotKeyDef.Presets[0];

    /// <summary>Human-readable state of the global hotkey, shown in the control panel.</summary>
    internal string HotKeyStatus { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A tray-resident app must not vanish because of one stray UI exception — that is exactly
        // how the gallery's re-entrant Close() took the whole process down, leaving no tray icon
        // and no explanation. Log and carry on; the log keeps such bugs diagnosable rather than
        // silently swallowed.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            args.Handled = true;
        };

        // Build-time helper: `Picky.exe --emit-icon <path>` writes the app .ico and exits.
        if (e.Args.Length == 2 && e.Args[0] == "--emit-icon")
        {
            System.IO.File.WriteAllBytes(
                e.Args[1],
                AppIcon.BuildIco(System.Drawing.Color.FromArgb(0x00, 0x78, 0xD4), System.Drawing.Color.White));
            Shutdown();
            return;
        }

        // Diagnostic helper: `Picky.exe --probe <folder>` writes the detected monitor layout
        // plus a capture of the whole virtual desktop and of each display, then exits.
        // Handy when a multi-monitor setup misbehaves.
        if (e.Args.Length == 2 && e.Args[0] == "--probe")
        {
            Diagnostics.RunProbe(e.Args[1]);
            Shutdown();
            return;
        }

        Settings = AppSettings.Load();

        if (AccentTheme.TryParse(Settings.AccentColor, out var accent))
        {
            AccentTheme.Apply(accent);
        }

        _hotKeys = new HotKeyService(() => Dispatcher.BeginInvoke(new Action(TriggerRegionCapture)));
        InitHotKey();

        // Start tray-resident with no window shown. Pressing the capture hotkey goes
        // straight to the snip overlay; the control panel opens on demand from the tray.
        _mainWindow = new MainWindow();

        _trayIcon = CreateTrayIcon();

        // Fire-and-forget: never block startup on a network call.
        _ = CheckForUpdatesAsync();

        // A tray utility can stay open for days, so a single startup check would miss any release
        // published in the meantime. Re-check every 6 hours until an update is staged (the check
        // then no-ops, and ApplyPendingUpdate restarts into the new build).
        _updateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(6),
        };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync();
        _updateTimer.Start();
    }

    /// <summary>
    /// Looks for a newer release and downloads it in the background.
    ///
    /// <para>Deliberately silent: a tray utility shouldn't interrupt with dialogs, so a staged update
    /// surfaces only as a tray menu entry plus a single balloon. Nothing is applied until the user
    /// asks, because restarting mid-capture or mid-recording would lose work.</para>
    ///
    /// <para>No-ops entirely unless running from a Velopack install, so debugging out of
    /// <c>bin\Debug</c> never touches the network.</para>
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        // Already downloaded and waiting for a restart — nothing more to do. Stop the periodic
        // timer so it doesn't re-check (and re-download) the same release.
        if (_pendingUpdate is not null)
        {
            _updateTimer?.Stop();
            return;
        }

        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));

            if (!manager.IsInstalled)
            {
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return;
            }

            await manager.DownloadUpdatesAsync(update);

            _updateManager = manager;
            _pendingUpdate = update;

            var version = update.TargetFullRelease.Version.ToString();
            _updateMenuItem.Text = $"Restart to update to v{version}";
            _updateMenuItem.Visible = true;
            _trayIcon.ShowBalloonTip(
                4000, "Picky", $"Update v{version} downloaded — restart to apply.", WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, or no release published yet. Log it; never nag the user.
            LogError(ex);
        }
    }

    /// <summary>Applies a staged update and relaunches.</summary>
    private void ApplyPendingUpdate()
    {
        if (_updateManager is null || _pendingUpdate is null)
        {
            return;
        }

        // Finish a recording first — restarting would otherwise orphan ffmpeg and truncate the MP4.
        if (_recorder.IsRecording)
        {
            StopRecording();
        }

        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }

/// <summary>Accent-tinted app glyph for Window.Icon (title bar + taskbar).</summary>
    /// <summary>
    /// Appends an unhandled exception to <c>%LocalAppData%\Picky\errors.log</c>. Logging must never
    /// itself throw, hence the blanket catch.
    /// </summary>
    private static void LogError(Exception exception)
    {
        try
        {
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Picky");
            System.IO.Directory.CreateDirectory(folder);

            System.IO.File.AppendAllText(
                System.IO.Path.Combine(folder, "errors.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing useful to do if even logging fails.
        }
    }

    internal System.Windows.Media.Imaging.BitmapSource CurrentIconSource()
        => AppIcon.CreateImageSource(ToDrawingColor(AccentTheme.Current), ToDrawingColor(AccentTheme.OnAccent));

    protected override void OnExit(ExitEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
        }
        _recordBorder.Hide();
        _hotKeys?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    private void InitHotKey()
    {
        var preferred = HotKeyDef.FromName(Settings.HotKey) ?? HotKeyDef.Presets[0];

        // Register the saved preference as-is (Win+Shift+S by default). No automatic
        // fallback — ApplyHotKey's status line tells the user if it couldn't be claimed.
        ApplyHotKey(preferred, persist: false);
    }

    /// <summary>Registers <paramref name="def"/> as the capture hotkey. Returns false if it's unavailable.</summary>
    internal bool ApplyHotKey(HotKeyDef def, bool persist)
    {
        bool ok = _hotKeys.Apply(def);

        if (ok)
        {
            CurrentHotKey = def;
        }

        if (persist)
        {
            Settings.HotKey = def.Name;
            Settings.Save();
        }

        HotKeyStatus = ok
            ? $"Global shortcut: {def.Name} — press it anywhere to snip."
            : def.Name == "Win+Shift+S"
                ? "Win+Shift+S is reserved by Windows — pick another shortcut."
                : $"{def.Name} is in use by another app — pick another.";

        return ok;
    }

    private void TriggerRegionCapture()
        => CaptureController.CaptureRegion();

    /// <summary>Applies a new accent color to the whole GUI, the snip overlay, and the tray icon.</summary>
    internal void ApplyAccent(System.Windows.Media.Color color, bool persist)
    {
        AccentTheme.Apply(color);

        var oldIcon = _trayIcon.Icon;
        _trayIcon.Icon = AppIcon.CreateIcon(ToDrawingColor(color), ToDrawingColor(AccentTheme.OnAccent));
        oldIcon?.Dispose();

        var iconSource = CurrentIconSource();
        foreach (Window window in Windows)
        {
            window.Icon = iconSource;
        }

        if (persist)
        {
            Settings.AccentColor = AccentTheme.ToHex(color);
            Settings.Save();
        }
    }

    private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color c)
        => System.Drawing.Color.FromArgb(c.R, c.G, c.B);

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        // Default light tray menu.
        var menu = new WinForms.ContextMenuStrip();

        // Only appears once an update has been downloaded and is waiting for a restart.
        _updateMenuItem = new WinForms.ToolStripMenuItem("Restart to update", null, (_, _) => ApplyPendingUpdate())
        {
            Visible = false,
        };
        menu.Items.Add(_updateMenuItem);

        menu.Items.Add("Capture region…", null, (_, _) => RunAfterMenuClosed(CaptureController.CaptureRegion));
        menu.Items.Add("Capture this screen", null, (_, _) => RunAfterMenuClosed(CaptureController.CaptureCurrentScreen));
        menu.Items.Add("Capture all screens", null, (_, _) => RunAfterMenuClosed(CaptureController.CaptureAllScreens));

        _captureScreenMenu = new WinForms.ToolStripMenuItem("Capture one screen");
        menu.Items.Add(_captureScreenMenu);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        _recordMenuItem = new WinForms.ToolStripMenuItem("Record region…", null, (_, _) => ToggleRecording());
        menu.Items.Add(_recordMenuItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Preferences", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        // Displays can be plugged, unplugged, rotated or rearranged while we sit in the
        // tray, so the per-display list is rebuilt each time the menu opens.
        menu.Opening += (_, _) => RebuildCaptureScreenMenu();

        var icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon.CreateIcon(ToDrawingColor(AccentTheme.Current), ToDrawingColor(AccentTheme.OnAccent)),
            Text = "Picky",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Left-click pops the gallery in the lower-right corner (dismiss-on-click-away);
        // right-click shows the capture / Preferences / Exit menu.
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                ShowGalleryDocked();
            }
        };

        return icon;
    }

    /// <summary>Repopulates the "Capture one screen" submenu from the live display list.</summary>
    private void RebuildCaptureScreenMenu()
    {
        _captureScreenMenu.DropDownItems.Clear();

        var monitors = MonitorInfo.All();

        // Pointless on a single-display setup — "Capture this screen" already covers it.
        _captureScreenMenu.Visible = monitors.Count > 1;

        foreach (var monitor in monitors)
        {
            var target = monitor; // capture per-iteration, not the loop variable
            _captureScreenMenu.DropDownItems.Add(
                monitor.Label,
                null,
                (_, _) => RunAfterMenuClosed(() => CaptureController.CaptureScreen(target)));
        }
    }

    /// <summary>
    /// Defers a capture until the tray menu has actually left the screen.
    /// The menu is a WinForms popup rather than a WPF <see cref="Window"/>, so the capture
    /// flow's hide-our-own-windows pass can't see it; without this delay the menu itself
    /// ends up baked into the screenshot.
    /// </summary>
    private void RunAfterMenuClosed(Action capture)
    {
        _trayIcon.ContextMenuStrip?.Close();

        // A DispatcherTimer (not Thread.Sleep) so the menu can finish painting itself away.
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            capture();
        };
        timer.Start();
    }

    public void ShowMainWindow()
    {
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    // --- Screen recording ---

    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    /// <summary>True while a recording is in progress.</summary>
    internal bool IsRecording => _recorder.IsRecording;

    /// <summary>Stops an in-progress recording (the gallery's Record/Stop button).</summary>
    internal void StopRecordingFromUi() => StopRecording();

    /// <summary>
    /// Starts a recording. Pass a rect in physical pixels to record it directly (a whole display,
    /// say), or null to let the user drag one out on the snip overlay.
    /// </summary>
    internal void StartRecording(Rectangle? region = null)
    {
        if (_recorder.IsRecording)
        {
            return;
        }

        if (!RecordingController.IsAvailable())
        {
            var choice = WinForms.MessageBox.Show(
                "Screen recording needs ffmpeg, which isn't installed.\n\n" +
                "Install it with:  winget install ffmpeg\n(or download it and add it to your PATH).\n\n" +
                "Open the ffmpeg download page now?",
                "Picky — ffmpeg required",
                WinForms.MessageBoxButtons.YesNo,
                WinForms.MessageBoxIcon.Information);

            if (choice == WinForms.DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://www.gyan.dev/ffmpeg/builds/") { UseShellExecute = true });
            }
            return;
        }

        Rectangle target;

        if (region is { } fixedRegion)
        {
            target = fixedRegion;
        }
        else
        {
            var overlay = new RegionSelectWindow();
            if (overlay.ShowDialog() != true)
            {
                return;
            }

            target = overlay.SelectedRegion;
        }

        var folder = Settings.EnsureCaptureFolder();
        var path = System.IO.Path.Combine(folder, $"Picky_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        if (!_recorder.Start(target, path, out var error))
        {
            WinForms.MessageBox.Show($"Couldn't start recording:\n{error}", "Picky");
            return;
        }

        _recordMenuItem.Text = "⏹ Stop recording";
        // Frame the area actually being captured (even-adjusted), drawn just outside it.
        _recordBorder.Show(_recorder.Region);
        ShowRecordBar();
    }

    private void StopRecording()
    {
        var path = _recorder.Stop();
        _recordBorder.Hide();
        _recordMenuItem.Text = "Record region…";

        _recordTimer?.Stop();
        _recordTimer = null;
        _recordBar?.Close();
        _recordBar = null;

        if (path is not null && System.IO.File.Exists(path))
        {
            // Same feedback as a screenshot: pop the docked gallery with the new clip selected,
            // rather than a balloon tip that disappears before you can act on it.
            ShowGalleryDocked(path);
        }
    }

    private void ShowRecordBar()
    {
        _recordStart = DateTime.Now;

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = System.Windows.Media.Brushes.Red,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var elapsed = new System.Windows.Controls.TextBlock
        {
            Text = "0:00",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            MinWidth = 34,
        };
        var stop = new System.Windows.Controls.Button
        {
            Content = "Stop",
            Style = (Style)Resources["AccentButton"],
            Padding = new Thickness(12, 4, 12, 4),
        };
        stop.Click += (_, _) => StopRecording();

        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(elapsed);
        row.Children.Add(stop);

        var shell = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)Resources["Brush.App"],
            BorderBrush = (System.Windows.Media.Brush)Resources["Brush.Stroke"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = row,
        };

        _recordBar = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = shell,
        };
        _recordBar.Loaded += (_, _) =>
            WindowPlacement.CenterTop(_recordBar!, MonitorInfo.FromCursor().WorkArea, marginPx: 8);
        _recordBar.Show();

        _recordTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordTimer.Tick += (_, _) =>
        {
            var t = DateTime.Now - _recordStart;
            elapsed.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        };
        _recordTimer.Start();
    }

    public void ShowGallery() => ShowGallery(dockLowerRight: false, selectPath: null);

    /// <summary>Pops the gallery anchored to the lower-right corner (Screenpresso-style).</summary>
    public void ShowGalleryDocked() => ShowGallery(dockLowerRight: true, selectPath: null);

    /// <summary>Pops the docked gallery and auto-selects the just-saved capture.</summary>
    public void ShowGalleryDocked(string selectPath) => ShowGallery(dockLowerRight: true, selectPath: selectPath);

    private void ShowGallery(bool dockLowerRight, string? selectPath)
    {
        if (_gallery is null)
        {
            _gallery = new GalleryWindow();
            _gallery.Closed += (_, _) => _gallery = null;
        }

        // When popped after a capture, the gallery hides itself the moment focus
        // leaves it (click outside → disappears, Screenpresso-style).
        _gallery.AutoCloseOnDeactivate = dockLowerRight;
        _gallery.Refresh();

        if (dockLowerRight)
        {
            DockLowerRight(_gallery);
        }

        if (!_gallery.IsVisible)
        {
            _gallery.Show();
        }

        _gallery.Activate();

        // Give a thumbnail an active selection + keyboard focus so an arrow key moves the
        // selection straight away: the just-captured shot when one was passed, else the
        // newest. Deferred to Loaded so the item containers exist.
        var gallery = _gallery;
        gallery.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (selectPath is not null)
                {
                    gallery.SelectByPath(selectPath);
                }
                else
                {
                    gallery.SelectFirst();
                }
            }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Positions a window flush to the lower-right of the work area of the monitor the pointer
    /// is on (above the taskbar). <c>SystemParameters.WorkArea</c> only ever describes the
    /// primary display, so using it would pin the popup there even when the capture happened
    /// on another screen.
    /// </summary>
    private static void DockLowerRight(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        WindowPlacement.DockToLowerRight(window, MonitorInfo.FromCursor().WorkArea);
    }
}
