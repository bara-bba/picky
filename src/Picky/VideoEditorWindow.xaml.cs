using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Picky.Native;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Rectangle = System.Windows.Shapes.Rectangle;
using Cursors = System.Windows.Input.Cursors;

namespace Picky;

/// <summary>
/// A small in-app video editor: play/scrub a recorded clip, drop markers (M), and act on the
/// span between the two markers bracketing the playhead — export it, or cut it out. Also grabs
/// the current frame as a screenshot and hands it to the existing <see cref="PreviewWindow"/>.
///
/// <para>Playback is WPF <see cref="MediaElement"/> (Media Foundation). Every edit is done by
/// <see cref="VideoEditor"/> through the bundled ffmpeg and writes a NEW file, so the original is
/// never modified. If Media Foundation can't decode the clip, the preview shows a notice but the
/// ffmpeg-based actions still work.</para>
/// </summary>
public partial class VideoEditorWindow : Window
{
    private readonly string _sourcePath;

    /// <summary>
    /// The clip currently loaded in the player. Edits (trim/remove) rewrite this working copy in
    /// place; <see cref="_dirty"/> then tracks whether it differs from <see cref="_sourcePath"/>.
    /// Starts equal to the source and only diverges once an edit is applied — at which point it
    /// points at a temp file under the scratch folder, never the original.
    /// </summary>
    private string _workingPath;

    /// <summary>True once an edit has been applied but not yet saved over the original or to a copy.</summary>
    private bool _dirty;

    /// <summary>Marker positions, kept sorted. The selection is the span between the two that
    /// bracket the playhead (or the nearest pair). Name-agnostic, list-based — no special "in/out".</summary>
    private readonly List<TimeSpan> _markers = new();

    private readonly DispatcherTimer _playheadTimer;
    private TimeSpan _duration;
    private bool _playing;
    private bool _mediaReady;

    // Timeline drag-to-select state: the press position, and whether the pointer has moved far
    // enough to count as a drag (vs. a plain click, which just seeks).
    private double _dragStartX;
    private bool _dragArmed;
    private bool _dragging;

    /// <summary>Small step for ← / →; a larger jump is applied with Shift.</summary>
    private static readonly TimeSpan SmallStep = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LargeStep = TimeSpan.FromSeconds(2);

    /// <summary>Opens the editor for one recorded clip.</summary>
    public VideoEditorWindow(string sourcePath)
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = App.CurrentIconSource();

        _sourcePath = sourcePath;
        _workingPath = sourcePath;
        Title = $"picky — {System.IO.Path.GetFileName(sourcePath)}";

        SelectionBand.Fill = SelectionBrush();

        // Drive the playhead/time readout while playing.
        _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playheadTimer.Tick += (_, _) => SyncPlayhead();

        Loaded += (_, _) =>
        {
            LoadWorking();
            Focus();
        };
    }

    /// <summary>(Re)loads <see cref="_workingPath"/> into the player and renders the first frame.</summary>
    private void LoadWorking()
    {
        _mediaReady = false;
        Player.Source = new Uri(_workingPath);
        // MediaElement needs a Play()/Pause() cycle to actually open and render the first frame.
        Player.Play();
        Player.Pause();
    }

    private static Brush SelectionBrush()
    {
        var c = AccentTheme.Current;
        c.A = 0x40;
        return new SolidColorBrush(c);
    }

    // --- Media lifecycle ---

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;

        // NaturalDuration is occasionally unavailable from WMF; fall back to an ffmpeg probe so the
        // timeline still scales correctly.
        if (_duration <= TimeSpan.Zero)
        {
            var seconds = VideoEditor.Probe(_workingPath);
            _duration = TimeSpan.FromSeconds(seconds);
        }

        RenderTimeline();
        SyncPlayhead();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        // Pause at the final frame rather than rewinding, so trimming near the end is easy.
        Player.Pause();
        _playing = false;
        _playheadTimer.Stop();
        PlayPauseButton.Content = "▶";
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // Can't preview — but ffmpeg edits don't need playback. Probe the duration so the timeline
        // and marker math still work, and tell the user preview is unavailable.
        _mediaReady = false;
        PlaybackError.Visibility = Visibility.Visible;

        var seconds = VideoEditor.Probe(_workingPath);
        _duration = TimeSpan.FromSeconds(seconds);
        RenderTimeline();
    }

    // --- Transport ---

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (!_mediaReady)
        {
            return;
        }

        if (_playing)
        {
            Player.Pause();
            _playing = false;
            _playheadTimer.Stop();
            PlayPauseButton.Content = "▶";
            SyncPlayhead();
        }
        else
        {
            // Restart from the beginning if parked at the very end.
            if (_duration > TimeSpan.Zero && Player.Position >= _duration - TimeSpan.FromMilliseconds(50))
            {
                Player.Position = TimeSpan.Zero;
            }

            Player.Play();
            _playing = true;
            _playheadTimer.Start();
            PlayPauseButton.Content = "⏸";
        }
    }

    private void Seek(TimeSpan to)
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        var clamped = to < TimeSpan.Zero ? TimeSpan.Zero : (to > _duration ? _duration : to);
        Player.Position = clamped;
        SyncPlayhead();
    }

    private void StepBy(TimeSpan delta) => Seek(Player.Position + delta);

    // --- Markers & selection ---

    private void AddMarker_Click(object sender, RoutedEventArgs e) => AddMarkerAtPlayhead();

    /// <summary>
    /// Drops a marker at the playhead. The selection is simply the span between the two markers, so
    /// the model is capped at two: the first <c>M</c> sets the start, the second sets the end, and a
    /// third replaces whichever existing marker is nearer (nudging that edge). This keeps the
    /// selection explicit and independent of where the playhead later drifts.
    /// </summary>
    private void AddMarkerAtPlayhead()
    {
        if (_duration <= TimeSpan.Zero)
        {
            return;
        }

        var at = Player.Position;

        // Ignore a duplicate within a frame's worth of an existing marker.
        if (_markers.Any(m => Math.Abs((m - at).TotalMilliseconds) < 40))
        {
            return;
        }

        if (_markers.Count < 2)
        {
            _markers.Add(at);
        }
        else
        {
            // Replace the nearer of the two existing markers — the user is adjusting that edge.
            int nearer = Math.Abs((_markers[0] - at).TotalMilliseconds) <= Math.Abs((_markers[1] - at).TotalMilliseconds)
                ? 0
                : 1;
            _markers[nearer] = at;
        }

        _markers.Sort();
        RenderTimeline();
        UpdateSelectionState();
    }

    /// <summary>The span to act on: simply the span between the two markers (order-independent).</summary>
    private (TimeSpan Start, TimeSpan End)? CurrentSelection()
    {
        if (_markers.Count < 2)
        {
            return null;
        }

        // _markers is kept sorted, so [0] is the start and [1] is the end.
        return (_markers[0], _markers[1]);
    }

    private void UpdateSelectionState()
    {
        var selection = CurrentSelection();
        bool has = selection is not null;

        RemoveButton.IsEnabled = has;

        if (!has || Timeline.ActualWidth <= 0 || _duration <= TimeSpan.Zero)
        {
            SelectionBand.Visibility = Visibility.Collapsed;
            return;
        }

        double x0 = XForTime(selection!.Value.Start);
        double x1 = XForTime(selection.Value.End);
        Canvas.SetLeft(SelectionBand, x0);
        SelectionBand.Width = Math.Max(0, x1 - x0);
        SelectionBand.Visibility = Visibility.Visible;
    }

    // --- Timeline rendering ---

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => RenderTimeline();

    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= TimeSpan.Zero || Timeline.ActualWidth <= 0)
        {
            return;
        }

        // Arm a possible drag-select; whether it becomes a drag or a plain seek is decided on move/up.
        _dragStartX = e.GetPosition(Timeline).X;
        _dragArmed = true;
        _dragging = false;
        Timeline.CaptureMouse();
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed
            || _duration <= TimeSpan.Zero || Timeline.ActualWidth <= 0)
        {
            return;
        }

        double x = e.GetPosition(Timeline).X;

        // Below the threshold it's still a click, not a drag.
        if (!_dragging && Math.Abs(x - _dragStartX) < 4)
        {
            return;
        }

        _dragging = true;

        // Drag start and current position become the two markers (sorted), so the selection band
        // and Keep/Remove follow the drag live.
        _markers.Clear();
        _markers.Add(TimeAtX(_dragStartX));
        _markers.Add(TimeAtX(x));
        _markers.Sort();

        RenderTimeline();
    }

    private void Timeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragArmed)
        {
            return;
        }

        _dragArmed = false;
        Timeline.ReleaseMouseCapture();

        double x = e.GetPosition(Timeline).X;

        if (_dragging)
        {
            // Finalise the dragged selection; drop a degenerate (too-short) drag.
            _dragging = false;
            if (_markers.Count == 2 && (_markers[1] - _markers[0]).TotalMilliseconds < 50)
            {
                _markers.Clear();
            }
            RenderTimeline();
        }
        else
        {
            // No drag — treat it as a plain seek to the clicked position.
            Seek(TimeAtX(x));
        }
    }

    /// <summary>Clip time at a horizontal position on the timeline.</summary>
    private TimeSpan TimeAtX(double x)
    {
        double fraction = Math.Clamp(x / Timeline.ActualWidth, 0, 1);
        return TimeSpan.FromTicks((long)(_duration.Ticks * fraction));
    }

    private void RenderTimeline()
    {
        // Clear old marker ticks (keep the band + playhead, which live in XAML).
        for (int i = Timeline.Children.Count - 1; i >= 0; i--)
        {
            if (Timeline.Children[i] is Rectangle r && ReferenceEquals(r.Tag, MarkerTag))
            {
                Timeline.Children.RemoveAt(i);
            }
        }

        if (_duration <= TimeSpan.Zero || Timeline.ActualWidth <= 0)
        {
            return;
        }

        var accent = (Brush)System.Windows.Application.Current.Resources["Brush.Accent"];
        foreach (var marker in _markers)
        {
            var tick = new Rectangle
            {
                Width = 2,
                Height = 42,
                Fill = accent,
                Tag = MarkerTag,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(tick, XForTime(marker));
            Canvas.SetTop(tick, 1);
            Timeline.Children.Add(tick);
        }

        UpdateSelectionState();
        SyncPlayhead();
    }

    private static readonly object MarkerTag = new();

    private double XForTime(TimeSpan t)
        => _duration <= TimeSpan.Zero ? 0 : Timeline.ActualWidth * (t.Ticks / (double)_duration.Ticks);

    private void SyncPlayhead()
    {
        var pos = Player.Position;

        if (_duration > TimeSpan.Zero && Timeline.ActualWidth > 0)
        {
            Canvas.SetLeft(Playhead, XForTime(pos));
        }

        // Just the elapsed / total time — kept short so it never overflows the transport row.
        TimeText.Text = $"{Format(pos)} / {Format(_duration)}";

        UpdateSelectionState();
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}";

    // --- Actions ---

    private void GrabFrame_Click(object sender, RoutedEventArgs e)
    {
        var folder = App.Settings.EnsureCaptureFolder();
        RunFfmpegAction(
            () => VideoEditor.GrabFrame(_workingPath, Player.Position.TotalSeconds, folder),
            result =>
            {
                // Open the saved frame in the screenshot editor — the same annotate/save flow.
                PreviewWindow.FromFile(result.OutputPath!).Show();
            });
    }

    /// <summary>Cuts the selected span out of the working clip and keeps the rest.</summary>
    private void RemoveSelection_Click(object sender, RoutedEventArgs e)
    {
        var selection = CurrentSelection();
        if (selection is null)
        {
            return;
        }

        var scratch = EditScratchPath();
        RunFfmpegAction(
            () => VideoEditor.RemoveSpanTo(_workingPath, selection.Value.Start.TotalSeconds, selection.Value.End.TotalSeconds, scratch),
            result => ApplyEdit(result.OutputPath!));
    }

    /// <summary>
    /// Makes <paramref name="editedPath"/> the new working copy: releases the player's lock on the
    /// current clip, reloads from the edited file, marks the editor dirty, and resets markers (the
    /// old positions no longer map onto the new, shorter timeline).
    /// </summary>
    private void ApplyEdit(string editedPath)
    {
        Player.Close(); // release the file handle on the previous working copy

        // Delete the previous working copy if it was itself a scratch edit (never the original).
        if (!string.Equals(_workingPath, _sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(_workingPath);
        }

        _workingPath = editedPath;
        _dirty = true;
        _markers.Clear();

        LoadWorking();
        UpdateSaveButtons();
    }

    // --- Save ---

    private void Save_Click(object sender, RoutedEventArgs e) => SaveOverOriginal();

    private void SaveCopy_Click(object sender, RoutedEventArgs e) => SaveCopy();

    /// <summary>Overwrites the original clip with the edited working copy.</summary>
    private bool SaveOverOriginal()
    {
        if (!_dirty)
        {
            return true;
        }

        try
        {
            // Release the player so the destination (and, when they're the same, the source) aren't
            // locked, copy the working copy over the original, then reload so playback continues.
            var resumeAt = Player.Position;
            Player.Close();
            System.IO.File.Copy(_workingPath, _sourcePath, overwrite: true);

            TryDelete(_workingPath);
            _workingPath = _sourcePath;
            _dirty = false;

            LoadWorking();
            UpdateSaveButtons();

            // Refresh the gallery so its thumbnail/duration re-render from the saved file.
            ((App)System.Windows.Application.Current).ShowGalleryDocked(_sourcePath);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save:\n{ex.Message}", "Picky — Video editor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>Saves the edited working copy to a new file the user picks, keeping the original.</summary>
    private bool SaveCopy()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save a copy",
            Filter = "MP4 video (*.mp4)|*.mp4",
            InitialDirectory = System.IO.Path.GetDirectoryName(_sourcePath),
            FileName = System.IO.Path.GetFileNameWithoutExtension(_sourcePath) + "_edited.mp4",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        try
        {
            System.IO.File.Copy(_workingPath, dialog.FileName, overwrite: true);
            _dirty = false;
            UpdateSaveButtons();
            ((App)System.Windows.Application.Current).ShowGalleryDocked(dialog.FileName);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save a copy:\n{ex.Message}", "Picky — Video editor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void UpdateSaveButtons()
    {
        SaveButton.IsEnabled = _dirty;
        SaveCopyButton.IsEnabled = _dirty;
    }

    /// <summary>A fresh temp path for one edit, under Picky's own cache folder (not the capture folder).</summary>
    private static string EditScratchPath()
    {
        var folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picky", "edits");
        System.IO.Directory.CreateDirectory(folder);
        return System.IO.Path.Combine(folder, $"edit_{Guid.NewGuid():N}.mp4");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch
        {
            // A lingering temp edit is harmless; it lives in Picky's own cache folder.
        }
    }

    /// <summary>
    /// Runs a (blocking) ffmpeg edit off the UI thread, disabling the action buttons meanwhile, then
    /// invokes <paramref name="onSuccess"/> back on the UI thread. Failures show a message.
    /// </summary>
    private void RunFfmpegAction(Func<VideoEditor.Result> operation, Action<VideoEditor.Result> onSuccess)
    {
        SetBusy(true);

        Task.Run(operation).ContinueWith(task =>
        {
            Dispatcher.Invoke(() =>
            {
                SetBusy(false);

                var result = task.Result;
                if (result.Ok)
                {
                    onSuccess(result);
                }
                else
                {
                    MessageBox.Show(this, result.Error ?? "The operation failed.", "Picky — Video editor",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
        });
    }

    private void SetBusy(bool busy)
    {
        RemoveButton.IsEnabled = !busy && CurrentSelection() is not null;
        FrameButton.IsEnabled = !busy;
        MarkerButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && _dirty;
        SaveCopyButton.IsEnabled = !busy && _dirty;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    // --- Keyboard ---

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (ctrl && e.Key == Key.S)
        {
            SaveOverOriginal();
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                e.Handled = true;
                break;

            case Key.M:
                AddMarkerAtPlayhead();
                e.Handled = true;
                break;

            case Key.Left:
                StepBy(ShiftHeld ? -LargeStep : -SmallStep);
                e.Handled = true;
                break;

            case Key.Right:
                StepBy(ShiftHeld ? LargeStep : SmallStep);
                e.Handled = true;
                break;

            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    private static bool ShiftHeld => (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Prompt before discarding unsaved trims, the same Save / Save a copy / Discard / Cancel
        // choice the screenshot editor offers.
        if (_dirty)
        {
            switch (AskSaveChanges())
            {
                case SaveChoice.Save:
                    if (!SaveOverOriginal()) { e.Cancel = true; return; }
                    break;
                case SaveChoice.SaveCopy:
                    if (!SaveCopy()) { e.Cancel = true; return; }
                    break;
                case SaveChoice.Discard:
                    break;
                default:
                    e.Cancel = true;
                    return;
            }
        }

        base.OnClosing(e);
    }

    private enum SaveChoice { Save, SaveCopy, Discard, Cancel }

    private SaveChoice AskSaveChanges()
    {
        var result = WinFormsMessageBox(
            "You've edited this clip. Save your changes to the original?",
            "Picky — Video editor");
        return result;
    }

    /// <summary>
    /// Four-way save prompt. Reused shape from the screenshot editor's dialog, kept minimal here:
    /// Yes = overwrite original, No = save a copy, Cancel = keep editing; discard is offered via a
    /// third explicit path below.
    /// </summary>
    private SaveChoice WinFormsMessageBox(string message, string title)
    {
        // A compact custom dialog so all four choices are explicit (a 3-button MessageBox can't show
        // Save / Save a copy / Discard / Cancel together).
        var choice = SaveChoice.Cancel;

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)System.Windows.Application.Current.Resources["Brush.App"],
            Foreground = (Brush)System.Windows.Application.Current.Resources["Brush.TextPrimary"],
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable, Segoe UI"),
        };

        var text = new TextBlock
        {
            Text = message,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(MakeDialogButton("Save", "AccentButton", () => { choice = SaveChoice.Save; dialog.Close(); }));
        buttons.Children.Add(MakeDialogButton("Save a copy…", "SubtleButton", () => { choice = SaveChoice.SaveCopy; dialog.Close(); }));
        buttons.Children.Add(MakeDialogButton("Don't save", "SubtleButton", () => { choice = SaveChoice.Discard; dialog.Close(); }));
        buttons.Children.Add(MakeDialogButton("Cancel", "SubtleButton", () => { choice = SaveChoice.Cancel; dialog.Close(); }));

        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(buttons);
        dialog.Content = new Border { Padding = new Thickness(20), Child = panel };

        DwmHelper.ApplyPowerToysChrome(dialog);
        dialog.ShowDialog();
        return choice;
    }

    private static Button MakeDialogButton(string content, string styleKey, Action onClick)
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

    protected override void OnClosed(EventArgs e)
    {
        _playheadTimer.Stop();
        Player.Close();

        // Drop a lingering scratch edit (the working copy was a temp file, never the original).
        if (!string.Equals(_workingPath, _sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(_workingPath);
        }

        base.OnClosed(e);
    }
}
