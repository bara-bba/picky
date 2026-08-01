using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Picky.Native;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Picky;

public partial class GalleryWindow : Window
{
    private readonly ObservableCollection<CaptureItem> _items = new();

    // Rubber-band (marquee) drag-select state.
    private Point _marqueeStart;
    private bool _marqueeActive;
    private bool _marqueeDragging;
    private List<object> _preDragSelection = new();

    // Drag-out state: the gallery acts like a folder, so selected cards can be dragged into
    // any app that accepts dropped files.
    private Point _dragStart;
    private bool _dragArmed;
    private bool _dragging;

    // Set when a mouse-down on an already-selected card was swallowed to preserve a
    // multi-selection; the deferred "collapse to just this card" runs on mouse-up instead.
    private ListBoxItem? _pendingSelectionCollapse;

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

        // Translucent accent for the marquee fill (was a hardcoded blue, which ignored the accent).
        var wash = AccentTheme.Current;
        wash.A = 0x33;
        MarqueeRect.Fill = new SolidColorBrush(wash);

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

        // A drag into another app deactivates us; closing mid-drag would abort the drop.
        if (AutoCloseOnDeactivate && !_suppressAutoClose && !_modalOpen && !_dragging)
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
            var directory = new DirectoryInfo(folder);

            var files = CaptureItem.SupportedPatterns
                .SelectMany(pattern => directory.GetFiles(pattern))
                // "*.jpg" can also match ".jpeg" via 8.3 short names, so de-duplicate by path.
                .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(f => f.LastWriteTime);

            foreach (var file in files)
            {
                _items.Add(new CaptureItem(file.FullName, file.LastWriteTime));
            }
        }

        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRecordButton();
    }

    // Double-click a thumbnail to open it: images go to the annotating preview, clips to
    // whatever plays video by default (PreviewWindow is an image editor and can't show them).
    private void Thumbnail_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not CaptureItem item
            || !File.Exists(item.Path))
        {
            return;
        }

        if (item.IsVideo)
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            return;
        }

        PreviewWindow.FromFile(item.Path).Show();
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

    /// <summary>
    /// Toolbar menus take focus away from the docked gallery, which would otherwise auto-close and
    /// take the menu with it. (The thumbnail menu does this via ContextMenuOpening.)
    /// </summary>
    private void ContextMenu_Opened(object sender, RoutedEventArgs e) => _suppressAutoClose = true;

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
        var card = FindAncestor<ListBoxItem>(src);

        if (card != null)
        {
            // Pressing a card arms a possible drag-out. Selection itself is left to the ListBox
            // so Ctrl/Shift-click keep working; we only take over once the pointer really moves.
            _dragStart = e.GetPosition(ThumbnailList);
            _dragArmed = true;

            bool modified = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;

            // Explorer semantics: pressing an already-selected card inside a multi-selection must
            // NOT collapse that selection, or dragging more than one file would be impossible.
            // Swallow the press and defer the collapse to mouse-up (skipped if a drag begins).
            // Only for single clicks, so double-click-to-open still reaches the ListBox.
            if (!modified && card.IsSelected && ThumbnailList.SelectedItems.Count > 1 && e.ClickCount == 1)
            {
                _pendingSelectionCollapse = card;
                e.Handled = true;
            }

            return;
        }

        // A click on the scrollbar is normal scrolling.
        if (FindAncestor<ScrollBar>(src) != null)
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
        // Drag-out takes priority: a press that began on a card becomes a file drag once the
        // pointer clears the system drag threshold.
        if (_dragArmed && !_dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            var moved = e.GetPosition(ThumbnailList) - _dragStart;

            if (Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                StartFileDrag();
                return;
            }
        }

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
        _dragArmed = false;

        // The press was swallowed to keep a multi-selection intact and no drag followed, so it
        // was really just a click: collapse to the card that was pressed, as Explorer does.
        if (_pendingSelectionCollapse is { } card)
        {
            _pendingSelectionCollapse = null;
            ThumbnailList.SelectedItems.Clear();
            card.IsSelected = true;
            card.Focus();
        }

        if (!_marqueeActive)
        {
            return;
        }

        _marqueeActive = false;
        _marqueeDragging = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        ThumbnailList.ReleaseMouseCapture();
    }

    /// <summary>
    /// Hands the selected captures to the OS as a file drop, so the gallery can be dragged into
    /// Slack, a browser, an email, Explorer — anything that accepts files from a folder.
    /// </summary>
    private void StartFileDrag()
    {
        _dragArmed = false;

        // A drag supersedes the deferred click-collapse; the whole selection travels.
        _pendingSelectionCollapse = null;

        var paths = ThumbnailList.SelectedItems
            .Cast<CaptureItem>()
            .Select(item => item.Path)
            .Where(File.Exists)
            .ToArray();

        if (paths.Length == 0)
        {
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, paths);
        // Some targets (editors, terminals) take text rather than files.
        data.SetData(DataFormats.Text, string.Join(Environment.NewLine, paths));

        // Marquee state must not survive into the nested drag loop, or the rectangle would be
        // left visible with the mouse still captured.
        _marqueeActive = false;
        _marqueeDragging = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        if (ThumbnailList.IsMouseCaptured)
        {
            ThumbnailList.ReleaseMouseCapture();
        }

        _dragging = true;
        try
        {
            // Copy only. Allowing Move would let a target relocate the original file out of the
            // capture folder, silently emptying the gallery.
            DragDrop.DoDragDrop(ThumbnailList, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
        }
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

    // --- Capture / record from the gallery toolbar ---

    /// <summary>
    /// Hides the gallery, waits for the hide to reach the screen, then runs the action — otherwise
    /// the gallery itself lands in the screenshot or the recording. Deliberately does not re-show:
    /// a capture re-opens the gallery with the new item, and a recording must keep it out of frame.
    /// </summary>
    private void RunHidden(Action action)
    {
        Hide();

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    /// <summary>The running app instance. Not named "Owner" — that would hide Window.Owner.</summary>
    private static App PickyApp => (App)System.Windows.Application.Current;

    // Left-click default: drag a region (the most common case).
    private void Screenshot_Click(object sender, RoutedEventArgs e) => RunHidden(CaptureController.CaptureRegion);

    private void ShotRegion_Click(object sender, RoutedEventArgs e) => RunHidden(CaptureController.CaptureRegion);

    private void ShotThisScreen_Click(object sender, RoutedEventArgs e) => RunHidden(CaptureController.CaptureCurrentScreen);

    private void ShotAllScreens_Click(object sender, RoutedEventArgs e) => RunHidden(CaptureController.CaptureAllScreens);

    /// <summary>Left-click toggles: stop if recording, otherwise record a dragged region.</summary>
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (PickyApp.IsRecording)
        {
            PickyApp.StopRecordingFromUi();
            UpdateRecordButton();
            return;
        }

        RunHidden(() => PickyApp.StartRecording(null));
    }

    private void RecordRegion_Click(object sender, RoutedEventArgs e)
    {
        if (PickyApp.IsRecording)
        {
            return;
        }

        RunHidden(() => PickyApp.StartRecording(null));
    }

    private void RecordScreen_Click(object sender, RoutedEventArgs e)
    {
        if (PickyApp.IsRecording)
        {
            return;
        }

        RunHidden(() => PickyApp.StartRecording(MonitorInfo.FromCursor().Bounds));
    }

    private void UpdateRecordButton()
        => RecordButton.Content = PickyApp.IsRecording ? "⏹  Stop" : "⏺  Record";

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
public sealed class CaptureItem : INotifyPropertyChanged
{
    /// <summary>Search patterns the gallery enumerates, images and clips alike.</summary>
    internal static readonly string[] SupportedPatterns = { "*.png", "*.jpg", "*.jpeg", "*.mp4" };

    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".mov" };

    private ImageSource? _thumbnail;
    private string _durationText = string.Empty;

    public string Path { get; }
    public string FileName { get; }
    public string TimeText { get; }

    /// <summary>True for recordings, which get a play badge and open in an external player.</summary>
    public bool IsVideo { get; }

    /// <summary>Clip length (e.g. "5:48"), filled in once ffmpeg has been consulted.</summary>
    public string DurationText
    {
        get => _durationText;
        private set
        {
            _durationText = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public CaptureItem(string path, DateTime time)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        TimeText = time.ToString("g");
        IsVideo = VideoExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

        if (IsVideo)
        {
            DurationText = "video";
            LoadVideoPosterAsync();
        }
        else
        {
            Thumbnail = LoadBitmap(path);
        }
    }

    /// <summary>
    /// A poster frame costs an ffmpeg round-trip, so it loads off the UI thread and fills in when
    /// ready — otherwise opening a folder of clips would freeze the gallery.
    /// </summary>
    private void LoadVideoPosterAsync()
    {
        var path = Path;

        Task.Run(() =>
        {
            var info = VideoThumbnailer.Probe(path);
            var poster = info.ThumbnailPath is null ? null : LoadBitmap(info.ThumbnailPath);
            var label = info.Duration > TimeSpan.Zero ? FormatDuration(info.Duration) : "video";

            // Frozen above, so handing it to the UI thread is safe.
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (poster is not null)
                {
                    Thumbnail = poster;
                }

                DurationText = label;
            });
        });
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{duration.Minutes}:{duration.Seconds:00}";

    private static ImageSource? LoadBitmap(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // decode now, don't lock the file
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 260;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze(); // required: may be created on a background thread
            return image;
        }
        catch
        {
            // Truncated or unreadable file — show the card without a preview.
            return null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
