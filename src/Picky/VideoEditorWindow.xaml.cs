using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Picky.Native;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Path = System.IO.Path;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfPath = System.Windows.Shapes.Path;

namespace Picky;

/// <summary>
/// Block-based clip editor, modelled on an NLE edit page (DaVinci Resolve's Edit page). The clip
/// sits on the timeline as blocks: split at the playhead (M) as many times as you like, click blocks
/// to select them (Ctrl = add/remove, Shift = range), delete them (the rest close up), or export
/// them. Ctrl+Z / Ctrl+Y undo and redo every edit.
///
/// <para>Edits are non-destructive and instant: the timeline is just an ordered list of kept source
/// ranges (<see cref="EditList"/>), and playback skips the removed parts live. ffmpeg only runs on
/// Export, Save or Save a copy, rendering the current blocks in one pass.</para>
/// </summary>
public partial class VideoEditorWindow : Window
{
    private readonly string _sourcePath;
    private readonly DispatcherTimer _timer;

    private IReadOnlyList<Segment> _segments = Array.Empty<Segment>();
    private readonly Stack<IReadOnlyList<Segment>> _undo = new();
    private readonly Stack<IReadOnlyList<Segment>> _redo = new();

    /// <summary>The edit last written to a copy, so closing right after "Save a copy" doesn't nag.</summary>
    private IReadOnlyList<Segment>? _savedCopy;

    private readonly HashSet<int> _selected = new();
    private int _anchor = -1;

    private TimeSpan _sourceDuration;
    private int _current;        // block the playhead is in
    private bool _playing;
    private bool _mediaReady;
    private bool _scrubbing;
    private bool _busy;
    private bool _allowClose;    // set once a close-time save has finished

    private static readonly TimeSpan SmallStep = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LargeStep = TimeSpan.FromSeconds(2);
    private static readonly object BlockTag = new();

    private static readonly Geometry PlayShape = Freeze(Geometry.Parse("M 0,0 L 11,6.5 L 0,13 Z"));
    private static readonly Geometry PauseShape = Freeze(Geometry.Parse("M 0,0 H 3.6 V 13 H 0 Z M 7.4,0 H 11 V 13 H 7.4 Z"));

    public VideoEditorWindow(string sourcePath)
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = App.CurrentIconSource();

        _sourcePath = sourcePath;
        Title = $"picky — {Path.GetFileName(sourcePath)}";
        SetPlayGlyph(playing: false);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => OnTick();

        Loaded += (_, _) =>
        {
            LoadSource();
            Focus();
        };
    }

    private static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }

    private bool Dirty => _segments.Count > 0
        && !EditList.IsWhole(_segments, _sourceDuration)
        && !ReferenceEquals(_segments, _savedCopy);

    private TimeSpan Total => EditList.Total(_segments);

    private TimeSpan ProgramPos => _segments.Count == 0
        ? TimeSpan.Zero
        : EditList.ToProgram(_segments, _current, Player.Position);

    // --- Media ---

    private void LoadSource()
    {
        _mediaReady = false;
        Player.Source = new Uri(_sourcePath);
        Player.Play(); // a Play/Pause cycle makes MediaElement open and show the first frame
        Player.Pause();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        var natural = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        InitTimeline(natural > TimeSpan.Zero ? natural : TimeSpan.FromSeconds(VideoEditor.Probe(_sourcePath)));
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // No preview, but blocks, export and frame capture still work through ffmpeg.
        _mediaReady = false;
        PlaybackError.Visibility = Visibility.Visible;
        InitTimeline(TimeSpan.FromSeconds(VideoEditor.Probe(_sourcePath)));
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e) => StopPlayback();

    /// <summary>First open (or reopen after a save): the timeline is one block, the whole clip.</summary>
    private void InitTimeline(TimeSpan duration)
    {
        _sourceDuration = duration;
        if (_segments.Count == 0 && duration > TimeSpan.Zero)
        {
            _segments = EditList.Whole(duration);
            _current = 0;
        }
        RenderBlocks();
        UpdateButtons();
    }

    // --- Transport ---

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (!_mediaReady || _segments.Count == 0)
        {
            return;
        }

        if (_playing)
        {
            StopPlayback();
            return;
        }

        if (Total - ProgramPos < TimeSpan.FromMilliseconds(50))
        {
            Seek(TimeSpan.Zero); // at the end: play again from the start
        }

        Player.Play();
        _playing = true;
        _timer.Start();
        SetPlayGlyph(playing: true);
    }

    private void StopPlayback()
    {
        Player.Pause();
        _playing = false;
        _timer.Stop();
        SetPlayGlyph(playing: false);
        SyncPlayhead();
    }

    /// <summary>While playing, jump over removed material at each block's end.</summary>
    private void OnTick()
    {
        if (_playing && _segments.Count > 0)
        {
            var block = _segments[_current];
            if (Player.Position >= block.End - TimeSpan.FromMilliseconds(20))
            {
                if (_current < _segments.Count - 1)
                {
                    _current++;
                    Player.Position = _segments[_current].Start;
                }
                else
                {
                    Player.Position = block.End;
                    StopPlayback();
                    return;
                }
            }
        }

        SyncPlayhead();
    }

    private void SetPlayGlyph(bool playing)
    {
        PlayPauseButton.Content = new WpfPath
        {
            Data = playing ? PauseShape : PlayShape,
            Fill = Brushes.White,
            Width = 11,
            Height = 13,
            Stretch = Stretch.Fill,
        };
    }

    /// <summary>Moves the playhead to a program time (clamped), mapping it to the right block.</summary>
    private void Seek(TimeSpan program)
    {
        if (_segments.Count == 0)
        {
            return;
        }

        var total = Total;
        program = program < TimeSpan.Zero ? TimeSpan.Zero : (program > total ? total : program);
        var (index, source) = EditList.ToSource(_segments, program);
        _current = index;
        Player.Position = source;
        SyncPlayhead();
    }

    /// <summary>Jumps to the previous / next cut (block edge), like Up/Down on an NLE timeline.</summary>
    private void JumpToCut(int direction)
    {
        var pos = ProgramPos;
        var edges = Enumerable.Range(0, _segments.Count + 1)
            .Select(i => EditList.ProgramStart(_segments, i))
            .ToList();
        var tolerance = TimeSpan.FromMilliseconds(30);

        var target = direction > 0
            ? edges.Where(t => t > pos + tolerance).DefaultIfEmpty(Total).First()
            : edges.Where(t => t < pos - tolerance).DefaultIfEmpty(TimeSpan.Zero).Last();
        Seek(target);
    }

    // --- Timeline ---

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => RenderBlocks();

    private TimeSpan ProgramAtX(double x)
    {
        double fraction = Math.Clamp(x / Math.Max(1, Timeline.ActualWidth), 0, 1);
        return TimeSpan.FromTicks((long)(Total.Ticks * fraction));
    }

    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_segments.Count == 0 || _busy)
        {
            return;
        }

        var program = ProgramAtX(e.GetPosition(Timeline).X);
        var (index, _) = EditList.ToSource(_segments, program);
        SelectBlock(index, Keyboard.Modifiers);

        // Pressing also parks the playhead there; keep dragging to scrub.
        _scrubbing = true;
        Timeline.CaptureMouse();
        Seek(program);
        Focus();
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (_scrubbing && e.LeftButton == MouseButtonState.Pressed)
        {
            Seek(ProgramAtX(e.GetPosition(Timeline).X));
        }
    }

    private void Timeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _scrubbing = false;
        Timeline.ReleaseMouseCapture();
    }

    /// <summary>Click = select one, Ctrl+click = add/remove, Shift+click = range from the last click.</summary>
    private void SelectBlock(int index, ModifierKeys modifiers)
    {
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;

        if (shift && _anchor >= 0)
        {
            _selected.Clear();
            for (int i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++)
            {
                _selected.Add(i);
            }
        }
        else if (ctrl)
        {
            if (!_selected.Remove(index))
            {
                _selected.Add(index);
            }
            _anchor = index;
        }
        else
        {
            _selected.Clear();
            _selected.Add(index);
            _anchor = index;
        }

        RenderBlocks();
        UpdateButtons();
    }

    private void RenderBlocks()
    {
        for (int i = Timeline.Children.Count - 1; i >= 0; i--)
        {
            if (Timeline.Children[i] is FrameworkElement el && ReferenceEquals(el.Tag, BlockTag))
            {
                Timeline.Children.RemoveAt(i);
            }
        }

        var total = Total;
        double width = Timeline.ActualWidth;
        if (total <= TimeSpan.Zero || width <= 0)
        {
            SyncPlayhead();
            return;
        }

        var accent = (Brush)System.Windows.Application.Current.Resources["Brush.Accent"];
        var onAccent = (Brush)System.Windows.Application.Current.Resources["Brush.OnAccent"];
        var idleFill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A));
        var idleEdge = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x50, 0x50, 0x50));
        var idleText = (Brush)System.Windows.Application.Current.Resources["Brush.TextSecondary"];

        for (int i = 0; i < _segments.Count; i++)
        {
            double x = width * (EditList.ProgramStart(_segments, i).Ticks / (double)total.Ticks);
            double w = width * (_segments[i].Length.Ticks / (double)total.Ticks);
            bool selected = _selected.Contains(i);

            var block = new Border
            {
                Tag = BlockTag,
                Width = Math.Max(2, w - 2),
                Height = 42,
                CornerRadius = new CornerRadius(3),
                Background = selected ? accent : idleFill,
                BorderBrush = selected ? accent : idleEdge,
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
                Child = w < 56 ? null : new TextBlock
                {
                    Text = $"{i + 1}   {Format(_segments[i].Length)}",
                    FontSize = 11,
                    Foreground = selected ? onAccent : idleText,
                    Margin = new Thickness(6, 4, 4, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Canvas.SetLeft(block, x + 1);
            Canvas.SetTop(block, 4);
            Timeline.Children.Add(block);
        }

        SyncPlayhead();
    }

    private void SyncPlayhead()
    {
        var total = Total;
        var pos = ProgramPos;
        if (total > TimeSpan.Zero && Timeline.ActualWidth > 0)
        {
            Canvas.SetLeft(Playhead, Timeline.ActualWidth * (pos.Ticks / (double)total.Ticks) - 1);
        }
        TimeText.Text = $"{Format(pos)} / {Format(total)}";
    }

    private static string Format(TimeSpan t)
        => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}";

    // --- Edits (all undoable) ---

    /// <summary>Commits a new block list onto the undo stack and parks the playhead at <paramref name="playheadAfter"/>.</summary>
    private void Commit(IReadOnlyList<Segment> next, TimeSpan playheadAfter)
    {
        if (ReferenceEquals(next, _segments))
        {
            return; // the operation was a no-op (cut on an edge, delete-everything refused)
        }

        _undo.Push(_segments);
        _redo.Clear();
        _segments = next;
        _selected.Clear();
        _anchor = -1;

        Seek(playheadAfter);
        RenderBlocks();
        UpdateButtons();
    }

    private void Split_Click(object sender, RoutedEventArgs e) => SplitAtPlayhead();

    private void SplitAtPlayhead()
    {
        if (_segments.Count == 0 || _busy)
        {
            return;
        }

        var pos = ProgramPos;
        Commit(EditList.Split(_segments, pos), pos);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_selected.Count == 0 || _busy)
        {
            return;
        }

        if (_selected.Count >= _segments.Count)
        {
            MessageBox.Show(this, "You can't delete every block — keep at least one.", "Picky — Video editor",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var landAt = EditList.ProgramStart(_segments, _selected.Min());
        Commit(EditList.Remove(_segments, _selected.ToList()), landAt);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo() => Restore(_undo, _redo);

    private void Redo() => Restore(_redo, _undo);

    private void Restore(Stack<IReadOnlyList<Segment>> from, Stack<IReadOnlyList<Segment>> to)
    {
        if (from.Count == 0 || _busy)
        {
            return;
        }

        var pos = ProgramPos;
        to.Push(_segments);
        _segments = from.Pop();
        _selected.Clear();
        _anchor = -1;

        Seek(pos);
        RenderBlocks();
        UpdateButtons();
    }

    private void SelectAll()
    {
        _selected.Clear();
        for (int i = 0; i < _segments.Count; i++)
        {
            _selected.Add(i);
        }
        RenderBlocks();
        UpdateButtons();
    }

    // --- Export / frame ---

    /// <summary>Renders the selected blocks, in timeline order, to a new clip in the capture folder.</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count == 0)
        {
            return;
        }

        var blocks = _selected.OrderBy(i => i).Select(i => _segments[i]).ToList();
        var output = VideoEditor.FreePath(App.Settings.EnsureCaptureFolder(), _sourcePath, "export", ".mp4");

        RunFfmpeg(
            () => VideoEditor.Render(_sourcePath, blocks, output),
            result => PickyApp.ShowGalleryDocked(result.OutputPath!));
    }

    private void GrabFrame_Click(object sender, RoutedEventArgs e)
    {
        var folder = App.Settings.EnsureCaptureFolder();
        var at = Player.Position.TotalSeconds;

        RunFfmpeg(
            () => VideoEditor.GrabFrame(_sourcePath, at, folder),
            result =>
            {
                // Lands in the gallery like any other screenshot, then opens for annotating.
                PickyApp.ShowGalleryDocked(result.OutputPath!);
                PickyApp.ShowEditor(PreviewWindow.FromFile(result.OutputPath!));
            });
    }

    // --- Save ---

    private void Save_Click(object sender, RoutedEventArgs e) => SaveOverOriginal(closeAfter: false);

    private void SaveCopy_Click(object sender, RoutedEventArgs e) => SaveCopy(closeAfter: false);

    /// <summary>Renders the edit to a temp file, then replaces the original with it and reloads.</summary>
    private void SaveOverOriginal(bool closeAfter)
    {
        if (!Dirty || _busy)
        {
            return;
        }

        var temp = VideoEditor.FreePath(EditsFolder(), _sourcePath, "render", Path.GetExtension(_sourcePath));
        var blocks = _segments;

        RunFfmpeg(
            () => VideoEditor.Render(_sourcePath, blocks, temp),
            _ =>
            {
                try
                {
                    StopPlayback();
                    Player.Close(); // release the original so it can be replaced
                    File.Copy(temp, _sourcePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Couldn't save:\n{ex.Message}", "Picky — Video editor",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    LoadSource();
                    return;
                }
                finally
                {
                    TryDelete(temp);
                }

                // The saved file is the new whole clip: start again from one block.
                _segments = Array.Empty<Segment>();
                _undo.Clear();
                _redo.Clear();
                _selected.Clear();
                _savedCopy = null;

                PickyApp.ShowGalleryDocked(_sourcePath);
                if (closeAfter)
                {
                    _allowClose = true;
                    Close();
                    return;
                }

                LoadSource();
                UpdateButtons();
            });
    }

    /// <summary>Renders the edit to a file the user picks, keeping the original untouched.</summary>
    private void SaveCopy(bool closeAfter)
    {
        if (_busy)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save a copy",
            Filter = "MP4 video (*.mp4)|*.mp4|QuickTime video (*.mov)|*.mov",
            InitialDirectory = Path.GetDirectoryName(_sourcePath),
            FileName = Path.GetFileNameWithoutExtension(_sourcePath) + "_edited.mp4",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var blocks = _segments;
        var output = dialog.FileName;

        RunFfmpeg(
            () => VideoEditor.Render(_sourcePath, blocks, output),
            result =>
            {
                _savedCopy = blocks;
                UpdateButtons();
                PickyApp.ShowGalleryDocked(result.OutputPath!);
                if (closeAfter)
                {
                    _allowClose = true;
                    Close();
                }
            });
    }

    private static string EditsFolder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picky", "edits");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover temp render in Picky's own cache folder is harmless.
        }
    }

    private static App PickyApp => (App)System.Windows.Application.Current;

    /// <summary>Runs an ffmpeg job off the UI thread with the toolbar disabled, then reports back.</summary>
    private void RunFfmpeg(Func<VideoEditor.Result> job, Action<VideoEditor.Result> onSuccess)
    {
        SetBusy(true);

        Task.Run(job).ContinueWith(task => Dispatcher.Invoke(() =>
        {
            SetBusy(false);

            var result = task.IsFaulted ? VideoEditor.Result.Fail("The operation failed.") : task.Result;
            if (result.Ok)
            {
                onSuccess(result);
            }
            else
            {
                MessageBox.Show(this, result.Error ?? "The operation failed.", "Picky — Video editor",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool idle = !_busy && _segments.Count > 0;
        SplitButton.IsEnabled = idle;
        FrameButton.IsEnabled = !_busy;
        DeleteButton.IsEnabled = idle && _selected.Count > 0 && _selected.Count < _segments.Count;
        ExportButton.IsEnabled = idle && _selected.Count > 0;
        UndoButton.IsEnabled = idle && _undo.Count > 0;
        RedoButton.IsEnabled = idle && _redo.Count > 0;
        SaveButton.IsEnabled = idle && Dirty;
        SaveCopyButton.IsEnabled = idle && !EditList.IsWhole(_segments, _sourceDuration);
    }

    // --- Keyboard ---

    /// <summary>Preview, so Space/arrows reach the editor even when a toolbar button has focus.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool handled = true;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z when shift: Redo(); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.S: SaveOverOriginal(closeAfter: false); break;
                case Key.A: SelectAll(); break;
                case Key.B: SplitAtPlayhead(); break;
                default: handled = false; break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.M: SplitAtPlayhead(); break;
                case Key.Delete:
                case Key.Back: DeleteSelected(); break;
                case Key.Left: Seek(ProgramPos - (shift ? LargeStep : SmallStep)); break;
                case Key.Right: Seek(ProgramPos + (shift ? LargeStep : SmallStep)); break;
                case Key.Up: JumpToCut(-1); break;
                case Key.Down: JumpToCut(+1); break;
                case Key.Home: Seek(TimeSpan.Zero); break;
                case Key.End: Seek(Total); break;
                case Key.Escape:
                    if (_selected.Count > 0)
                    {
                        _selected.Clear();
                        RenderBlocks();
                        UpdateButtons();
                    }
                    else
                    {
                        Close();
                    }
                    break;
                default: handled = false; break;
            }
        }

        e.Handled = handled;
        base.OnPreviewKeyDown(e);
    }

    // --- Closing ---

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true; // don't close mid-render
            return;
        }

        if (!_allowClose && Dirty)
        {
            switch (AskSaveChanges())
            {
                case SaveChoice.Save:
                    e.Cancel = true;
                    SaveOverOriginal(closeAfter: true); // closes itself once the render is done
                    return;
                case SaveChoice.SaveCopy:
                    e.Cancel = true;
                    SaveCopy(closeAfter: true);
                    return;
                case SaveChoice.Discard:
                    break;
                default:
                    e.Cancel = true;
                    return;
            }
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        Player.Close();
        base.OnClosed(e);
    }

    private enum SaveChoice { Save, SaveCopy, Discard, Cancel }

    /// <summary>Save / Save a copy / Don't save / Cancel, styled like the screenshot editor's prompt.</summary>
    private SaveChoice AskSaveChanges()
    {
        var choice = SaveChoice.Cancel;
        var resources = System.Windows.Application.Current.Resources;

        var dialog = new Window
        {
            Title = "Save changes?",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)resources["Brush.App"],
            Foreground = (Brush)resources["Brush.TextPrimary"],
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable, Segoe UI"),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(DialogButton("Save", "AccentButton", () => { choice = SaveChoice.Save; dialog.Close(); }));
        buttons.Children.Add(DialogButton("Save a copy…", "SubtleButton", () => { choice = SaveChoice.SaveCopy; dialog.Close(); }));
        buttons.Children.Add(DialogButton("Don't save", "SubtleButton", () => { choice = SaveChoice.Discard; dialog.Close(); }));
        buttons.Children.Add(DialogButton("Cancel", "SubtleButton", () => { choice = SaveChoice.Cancel; dialog.Close(); }));

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "You've edited this clip. Save your changes?",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            Margin = new Thickness(0, 0, 0, 16),
        });
        panel.Children.Add(buttons);
        dialog.Content = new Border { Padding = new Thickness(20), Child = panel };

        DwmHelper.ApplyPowerToysChrome(dialog);
        dialog.ShowDialog();
        return choice;
    }

    private static Button DialogButton(string content, string styleKey, Action onClick)
    {
        var button = new Button
        {
            Content = content,
            Style = (Style)System.Windows.Application.Current.Resources[styleKey],
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
