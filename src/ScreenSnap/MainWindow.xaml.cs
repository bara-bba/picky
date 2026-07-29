using System.Windows;
using System.Windows.Controls;
using ScreenSnap.Native;

namespace ScreenSnap;

public partial class MainWindow : Window
{
    private bool _initializing = true;

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
