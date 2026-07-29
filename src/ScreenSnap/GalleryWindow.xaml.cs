using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenSnap.Native;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ScreenSnap;

public partial class GalleryWindow : Window
{
    private readonly ObservableCollection<CaptureItem> _items = new();

    // Rubber-band (marquee) drag-select state.
    private Point _marqueeStart;
    private bool _marqueeActive;
    private bool _marqueeDragging;
    private List<object> _preDragSelection = new();

    // Keyboard navigation state: the moving end (_currentIndex) and the fixed end
    // (_selectionAnchor) of a Shift+Arrow selection, walked in flat reading order.
    private int _currentIndex = -1;
    private int _selectionAnchor = -1;
    private bool _syncingSelection;

    /// <summary>When true (docked-after-capture mode), the window hides as soon as it loses focus.</summary>
    public bool AutoCloseOnDeactivate { get; set; }

    // Set while a context menu is open, so losing focus to it doesn't auto-close the popup.
    private bool _suppressAutoClose;

    // Set while we host a modal dialog (folder picker, rename) — survives the context menu
    // closing, which would otherwise clear _suppressAutoClose out from under the dialog.
    private bool _modalOpen;

    public GalleryWindow()
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = ((App)System.Windows.Application.Current).CurrentIconSource();
        ThumbnailList.ItemsSource = _items;
        Refresh();
    }

    /// <summary>Selects and focuses the newest thumbnail so arrow keys navigate immediately.</summary>
    public void SelectFirst() => SelectIndex(0);

    /// <summary>Selects the capture with the given file path (falls back to the newest).</summary>
    public void SelectByPath(string path)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                SelectIndex(i);
                return;
            }
        }

        SelectIndex(0);
    }

    private void SelectIndex(int index)
    {
        if (_items.Count == 0)
        {
            ThumbnailList.Focus();
            return;
        }

        index = Math.Clamp(index, 0, _items.Count - 1);
        ThumbnailList.UpdateLayout();

        _syncingSelection = true;
        ThumbnailList.SelectedIndex = index;
        _selectionAnchor = index;
        _currentIndex = index;
        _syncingSelection = false;

        ThumbnailList.ScrollIntoView(_items[index]);
        if (ThumbnailList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container)
        {
            container.Focus();
        }
        else
        {
            ThumbnailList.Focus();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (AutoCloseOnDeactivate && !_suppressAutoClose && !_modalOpen)
        {
            Close();
        }
    }

    public void Refresh()
    {
        _items.Clear();

        var folder = App.Settings.CaptureFolder;
        FolderPathText.Text = folder;

        if (Directory.Exists(folder))
        {
            var files = new DirectoryInfo(folder)
                .GetFiles("*.png")
                .OrderByDescending(f => f.LastWriteTime);

            foreach (var file in files)
            {
                _items.Add(new CaptureItem(file.FullName, file.LastWriteTime));
            }
        }

        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Double-click a thumbnail to open it in the full preview.
    private void Thumbnail_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is CaptureItem item
            && File.Exists(item.Path))
        {
            PreviewWindow.FromFile(item.Path).Show();
        }
    }

    // Right-click selects the card under the cursor (unless it's already part of the
    // current selection), so the context menu acts on what you clicked.
    private void ThumbnailList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { IsSelected: false } item)
        {
            ThumbnailList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) => CopySelectedPaths();

    private void CopySelectedPaths()
    {
        var paths = ThumbnailList.SelectedItems.Cast<CaptureItem>().Select(i => i.Path);
        var text = string.Join(Environment.NewLine, paths);
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }

    // Keep the docked popup alive while its context menu is open (it would otherwise
    // auto-close when the menu takes focus). Rename only makes sense for a single image.
    private void ThumbnailList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _suppressAutoClose = true;
        RenameMenuItem.Visibility = ThumbnailList.SelectedItems.Count == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ThumbnailList.SelectedItems.Count != 1 || ThumbnailList.SelectedItems[0] is not CaptureItem item)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(item.Path)!;
        var extension = System.IO.Path.GetExtension(item.Path);
        var currentName = System.IO.Path.GetFileNameWithoutExtension(item.Path);

        _modalOpen = true;
        try
        {
            var input = PromptRename(currentName);
            if (input is null)
            {
                return;
            }

            input = input.Trim();
            if (input.Length == 0 || input == currentName)
            {
                return;
            }

            if (input.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "That name contains characters that aren't allowed in a file name.",
                    "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var newPath = System.IO.Path.Combine(directory, input + extension);
            if (File.Exists(newPath))
            {
                MessageBox.Show(this, "A file with that name already exists.",
                    "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                File.Move(item.Path, newPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't rename:\n{ex.Message}", "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Refresh();
            SelectByPath(newPath);
        }
        finally
        {
            _modalOpen = false;
            Activate();
        }
    }

    private string? PromptRename(string currentName)
    {
        string? result = null;

        var dialog = new Window
        {
            Title = "Rename",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Brush.App"],
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Brush.TextPrimary"],
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable, Segoe UI"),
        };

        var label = new TextBlock { Text = "New name:", Margin = new Thickness(0, 0, 0, 8) };
        var box = new TextBox
        {
            Text = currentName,
            MinWidth = 300,
            Style = (Style)System.Windows.Application.Current.Resources["DarkTextBox"],
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var rename = MakeDialogButton("Rename", "AccentButton", () => { result = box.Text; dialog.Close(); });
        var cancel = MakeDialogButton("Cancel", "SubtleButton", () => { result = null; dialog.Close(); });
        buttons.Children.Add(rename);
        buttons.Children.Add(cancel);

        box.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter) { result = box.Text; dialog.Close(); }
            else if (ev.Key == Key.Escape) { result = null; dialog.Close(); }
        };

        var panel = new StackPanel();
        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        dialog.Content = new Border { Padding = new Thickness(20), Child = panel };

        DwmHelper.ApplyPowerToysChrome(dialog);
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        dialog.ShowDialog();
        return result;
    }

    private static Button MakeDialogButton(string text, string styleKey, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)System.Windows.Application.Current.Resources[styleKey],
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void ContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _suppressAutoClose = false;
        Activate();
    }

    private void ThumbnailList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Canc / Delete removes the selected captures (to the Recycle Bin, so they're recoverable).
        if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
            return;
        }

        // Ctrl+C copies the selected file path(s) — skips the right-click menu.
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopySelectedPaths();
            e.Handled = true;
            return;
        }

        // Arrow keys navigate in flat reading order so Shift+Left wraps to the previous
        // row's last card (not just within the current visual column).
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            HandleArrowNavigation(e.Key);
            e.Handled = true;
        }
    }

    // A mouse / marquee selection re-seats the keyboard anchor on the block's bottom edge
    // and the moving end on its top edge, so a following Shift+Up grows upward by whole rows.
    private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || ThumbnailList.SelectedItems.Count == 0)
        {
            return;
        }

        int min = int.MaxValue;
        int max = -1;
        foreach (var obj in ThumbnailList.SelectedItems)
        {
            int idx = _items.IndexOf((CaptureItem)obj);
            if (idx < 0)
            {
                continue;
            }

            min = Math.Min(min, idx);
            max = Math.Max(max, idx);
        }

        if (max < 0)
        {
            return;
        }

        _selectionAnchor = max; // fixed end (bottom of the block)
        _currentIndex = min;    // moving end (top of the block)
    }

    private void HandleArrowNavigation(Key key)
    {
        int count = _items.Count;
        if (count == 0)
        {
            return;
        }

        int cols = ColumnsPerRow();
        int current = (_currentIndex >= 0 && _currentIndex < count)
            ? _currentIndex
            : Math.Max(0, ThumbnailList.SelectedIndex);

        int delta = key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -cols,
            Key.Down => cols,
            _ => 0,
        };

        int target = Math.Clamp(current + delta, 0, count - 1);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        _syncingSelection = true;
        if (shift)
        {
            if (_selectionAnchor < 0 || _selectionAnchor >= count)
            {
                _selectionAnchor = current;
            }

            SelectLinearRange(_selectionAnchor, target);
        }
        else
        {
            ThumbnailList.SelectedItems.Clear();
            ThumbnailList.SelectedIndex = target;
            _selectionAnchor = target;
        }

        _currentIndex = target;
        _syncingSelection = false;

        ThumbnailList.ScrollIntoView(_items[target]);
        if (ThumbnailList.ItemContainerGenerator.ContainerFromIndex(target) is ListBoxItem container)
        {
            container.Focus();
        }
    }

    private void SelectLinearRange(int a, int b)
    {
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);

        ThumbnailList.SelectedItems.Clear();
        for (int i = lo; i <= hi; i++)
        {
            ThumbnailList.SelectedItems.Add(_items[i]);
        }
    }

    // How many cards sit in the top row of the current wrap layout.
    private int ColumnsPerRow()
    {
        if (_items.Count == 0
            || ThumbnailList.ItemContainerGenerator.ContainerFromIndex(0) is not ListBoxItem first)
        {
            return 1;
        }

        double firstY = first.TransformToAncestor(ThumbnailList).Transform(new Point(0, 0)).Y;
        int cols = 0;

        for (int i = 0; i < _items.Count; i++)
        {
            if (ThumbnailList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c)
            {
                break;
            }

            double y = c.TransformToAncestor(ThumbnailList).Transform(new Point(0, 0)).Y;
            if (Math.Abs(y - firstY) > 1)
            {
                break; // reached the next row
            }

            cols++;
        }

        return Math.Max(1, cols);
    }

    private void DeleteSelected()
    {
        var selected = ThumbnailList.SelectedItems.Cast<CaptureItem>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var item in selected)
        {
            try
            {
                if (File.Exists(item.Path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        item.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            }
            catch
            {
                // Skip files that can't be deleted (locked, already gone); the rest still go.
            }
        }

        Refresh();
    }

    // --- Rubber-band marquee selection over the thumbnail grid ---

    private void ThumbnailList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;

        // A click on an actual card (or the scrollbar) is normal ListBox selection/scrolling.
        if (FindAncestor<ListBoxItem>(src) != null || FindAncestor<ScrollBar>(src) != null)
        {
            return;
        }

        _marqueeStart = e.GetPosition(ThumbnailList);
        _marqueeActive = true;
        _marqueeDragging = false;
        // Ctrl+drag adds to the existing selection; a plain drag replaces it.
        _preDragSelection = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            ? ThumbnailList.SelectedItems.Cast<object>().ToList()
            : new List<object>();
        ThumbnailList.CaptureMouse();
    }

    private void ThumbnailList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_marqueeActive || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(ThumbnailList);

        if (!_marqueeDragging)
        {
            if ((current - _marqueeStart).Length < 4)
            {
                return; // treat tiny movements as a click, not a drag
            }

            _marqueeDragging = true;
            MarqueeRect.Visibility = Visibility.Visible;
        }

        var rect = new Rect(_marqueeStart, current);
        Canvas.SetLeft(MarqueeRect, rect.Left);
        Canvas.SetTop(MarqueeRect, rect.Top);
        MarqueeRect.Width = rect.Width;
        MarqueeRect.Height = rect.Height;

        foreach (var obj in ThumbnailList.Items)
        {
            if (ThumbnailList.ItemContainerGenerator.ContainerFromItem(obj) is not ListBoxItem container)
            {
                continue;
            }

            container.IsSelected = rect.IntersectsWith(ItemBounds(container)) || _preDragSelection.Contains(obj);
        }

        e.Handled = true;
    }

    private void ThumbnailList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_marqueeActive)
        {
            return;
        }

        _marqueeActive = false;
        _marqueeDragging = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        ThumbnailList.ReleaseMouseCapture();
    }

    private Rect ItemBounds(ListBoxItem container)
    {
        var topLeft = container.TransformToAncestor(ThumbnailList).Transform(new Point(0, 0));
        return new Rect(topLeft, new Size(container.ActualWidth, container.ActualHeight));
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null && source is not T)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Preferences_Click(object sender, RoutedEventArgs e)
        => ((App)System.Windows.Application.Current).ShowMainWindow();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = App.Settings.EnsureCaptureFolder();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose capture folder",
            InitialDirectory = App.Settings.CaptureFolder,
        };

        bool picked;
        _suppressAutoClose = true; // don't self-close when the modal picker takes focus
        try
        {
            picked = dialog.ShowDialog(this) == true;
        }
        finally
        {
            _suppressAutoClose = false;
        }

        if (picked)
        {
            App.Settings.CaptureFolder = dialog.FolderName;
            App.Settings.Save();
            Refresh();
        }

        Activate();
    }
}

/// <summary>One entry in the gallery: a lazily-decoded thumbnail plus metadata.</summary>
public sealed class CaptureItem
{
    public string Path { get; }
    public string FileName { get; }
    public string TimeText { get; }
    public ImageSource Thumbnail { get; }

    public CaptureItem(string path, DateTime time)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        TimeText = time.ToString("g");
        Thumbnail = LoadThumbnail(path);
    }

    private static ImageSource LoadThumbnail(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // decode now, don't lock the file
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = 260;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
