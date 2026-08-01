using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Picky.Native;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Picky;

public partial class MainWindow : Window
{
    private bool _initializing = true;
    private bool _initializingStartup;

    public MainWindow()
    {
        InitializeComponent();
        // NOTE: Mica compositing through WPF requires Background to stay
        // Transparent while AllowsTransparency is False; DWM paints the
        // backdrop behind the HWND once DwmHelper sets DWMWA_SYSTEMBACKDROP_TYPE.
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = App.CurrentIconSource();
        UpdateFolderPath();
        InitHotKeyPicker();
        AccentHexInput.Text = AccentTheme.ToHex(AccentTheme.Current);
        BuildPenColors();
        InitStartWithWindows();
    }

    /// <summary>
    /// Reflects the *registry* rather than the saved preference: the user can remove the entry from
    /// Task Manager's Startup tab or Windows Settings without Picky being told, so the Run key is
    /// the source of truth. Any drift is written back to settings.
    /// </summary>
    private void InitStartWithWindows()
    {
        bool actuallyEnabled = StartupRegistration.IsEnabled();

        _initializingStartup = true;
        StartWithWindowsCheck.IsChecked = actuallyEnabled;
        _initializingStartup = false;

        if (App.Settings.StartWithWindows != actuallyEnabled)
        {
            App.Settings.StartWithWindows = actuallyEnabled;
            App.Settings.Save();
        }

        StartupStatusText.Text = actuallyEnabled
            ? "Picky will launch when you sign in."
            : string.Empty;
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingStartup)
        {
            return;
        }

        bool wanted = StartWithWindowsCheck.IsChecked == true;

        if (StartupRegistration.SetEnabled(wanted))
        {
            App.Settings.StartWithWindows = wanted;
            App.Settings.Save();
            StartupStatusText.Text = wanted ? "Picky will launch when you sign in." : string.Empty;
            return;
        }

        // Registry refused — put the checkbox back so it can't claim something untrue.
        _initializingStartup = true;
        StartWithWindowsCheck.IsChecked = !wanted;
        _initializingStartup = false;
        StartupStatusText.Text = "Couldn't update the Windows startup entry.";
    }

    private void BuildPenColors()
    {
        string[] hexes =
        {
            "#E81123", "#FF8C00", "#FFB900", "#16C60C",
            "#0078D4", "#8E44AD", "#000000", "#FFFFFF",
        };

        foreach (var hex in hexes)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            var inner = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(color),
            };
            var ring = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Tag = hex,
                Child = inner,
            };
            ring.MouseLeftButtonDown += PenColor_Click;
            PenColorPanel.Children.Add(ring);
        }

        HighlightPenColor(App.Settings.DefaultColor);
    }

    private void PenColor_Click(object sender, MouseButtonEventArgs e)
    {
        var hex = (string)((Border)sender).Tag;
        App.Settings.DefaultColor = hex;
        App.Settings.Save();
        HighlightPenColor(hex);
    }

    private void HighlightPenColor(string hex)
    {
        // Accent, read live from resources, so the indicator matches every other selection cue in
        // the app instead of being a fixed white ring.
        var accent = (Brush)System.Windows.Application.Current.Resources["Brush.Accent"];

        foreach (Border ring in PenColorPanel.Children)
        {
            ring.BorderBrush = string.Equals((string)ring.Tag, hex, System.StringComparison.OrdinalIgnoreCase)
                ? accent
                : Brushes.Transparent;
        }
    }

    private App App => (App)System.Windows.Application.Current;

    private void InitHotKeyPicker()
    {
        HotKeyCombo.ItemsSource = HotKeyDef.Presets;
        HotKeyCombo.SelectedItem = App.CurrentHotKey;
        HotKeyStatusText.Text = App.HotKeyStatus;
        _initializing = false;
    }

    private void HotKeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || HotKeyCombo.SelectedItem is not HotKeyDef def)
        {
            return;
        }

        App.ApplyHotKey(def, persist: true);
        HotKeyStatusText.Text = App.HotKeyStatus;
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DragMove();
    }

    // Close hides Preferences to the tray (Screenpresso-style); Exit lives in the tray menu.
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void GalleryButton_Click(object sender, RoutedEventArgs e)
        => ((App)System.Windows.Application.Current).ShowGalleryDocked();

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose capture folder",
            InitialDirectory = App.Settings.CaptureFolder,
        };

        if (dialog.ShowDialog() == true)
        {
            App.Settings.CaptureFolder = dialog.FolderName;
            App.Settings.Save();
            UpdateFolderPath();
        }
    }

    private void UpdateFolderPath()
    {
        FolderPathText.Text = App.Settings.CaptureFolder;
        FolderPathText.ToolTip = App.Settings.CaptureFolder;
    }

    private void AccentHexInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            TryApplyAccent();
        }
    }

    private void ApplyAccent_Click(object sender, RoutedEventArgs e) => TryApplyAccent();

    private void TryApplyAccent()
    {
        if (AccentTheme.TryParse(AccentHexInput.Text, out var color))
        {
            App.ApplyAccent(color, persist: true);
            AccentHexInput.Text = AccentTheme.ToHex(color); // normalize (e.g. add '#', uppercase)
        }
        else
        {
            // Invalid hex — revert the box to the current accent.
            AccentHexInput.Text = AccentTheme.ToHex(AccentTheme.Current);
        }
    }
}
