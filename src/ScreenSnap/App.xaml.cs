using System.Windows;
using WinForms = System.Windows.Forms;

namespace ScreenSnap;

public partial class App : Application
{
    private WinForms.NotifyIcon _trayIcon = null!;
    private MainWindow _mainWindow = null!;
    private GalleryWindow? _gallery;
    private HotKeyService _hotKeys = null!;

    internal static AppSettings Settings { get; private set; } = null!;

    /// <summary>The hotkey currently registered (may differ from the saved preference on fallback).</summary>
    internal HotKeyDef CurrentHotKey { get; private set; } = HotKeyDef.Presets[0];

    /// <summary>Human-readable state of the global hotkey, shown in the control panel.</summary>
    internal string HotKeyStatus { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Build-time helper: `ScreenSnap.exe --emit-icon <path>` writes the app .ico and exits.
        if (e.Args.Length == 2 && e.Args[0] == "--emit-icon")
        {
            System.IO.File.WriteAllBytes(
                e.Args[1],
                AppIcon.BuildIco(System.Drawing.Color.FromArgb(0x00, 0x78, 0xD4), System.Drawing.Color.White));
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
    }

    /// <summary>Accent-tinted app glyph for Window.Icon (title bar + taskbar).</summary>
    internal System.Windows.Media.Imaging.BitmapSource CurrentIconSource()
        => AppIcon.CreateImageSource(ToDrawingColor(AccentTheme.Current), ToDrawingColor(AccentTheme.OnAccent));

    protected override void OnExit(ExitEventArgs e)
    {
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
        menu.Items.Add("Preferences", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        var icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon.CreateIcon(ToDrawingColor(AccentTheme.Current), ToDrawingColor(AccentTheme.OnAccent)),
            Text = "ScreenSnap",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Left-click pops the gallery in the lower-right corner (dismiss-on-click-away);
        // right-click shows the Preferences / Exit menu.
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                ShowGalleryDocked();
            }
        };

        return icon;
    }

    public void ShowMainWindow()
    {
        _mainWindow.Show();
        _mainWindow.Activate();
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

    /// <summary>Positions a window flush to the lower-right of the working area (above the taskbar).</summary>
    private static void DockLowerRight(Window window)
    {
        var work = SystemParameters.WorkArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = work.Right - window.Width;
        window.Top = work.Bottom - window.Height;
    }
}
