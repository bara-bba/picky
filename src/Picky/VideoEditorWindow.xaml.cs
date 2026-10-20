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

    /// <summary>Marker positions, kept sorted. The selection is the span between the two that
    /// bracket the playhead (or the nearest pair). Name-agnostic, list-based — no special "in/out".</summary>
    private readonly List<TimeSpan> _markers = new();

    private readonly DispatcherTimer _playheadTimer;
    private TimeSpan _duration;
    private bool _playing;
    private bool _mediaReady;

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
        Title = $"picky — {System.IO.Path.GetFileName(sourcePath)}";

        SelectionBand.Fill = SelectionBrush();

        // Drive the playhead/time readout while playing.
        _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playheadTimer.Tick += (_, _) => SyncPlayhead();

        Loaded += (_, _) =>
        {
            Player.Source = new Uri(_sourcePath);
            // MediaElement needs a Play()/Pause() cycle to actually open and render the first frame.
            Player.Play();
            Player.Pause();
            Focus();
        };
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
            var seconds = VideoEditor.Probe(_sourcePath);
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

        var seconds = VideoEditor.Probe(_sourcePath);
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

        _markers.Add(at);
        _markers.Sort();
        RenderTimeline();
        UpdateSelectionState();
    }

    /// <summary>The span acted on: the two markers bracketing the playhead, else the nearest pair.</summary>
    private (TimeSpan Start, TimeSpan End)? CurrentSelection()
    {
        if (_markers.Count < 2)
        {
            return null;
        }

        var pos = Player.Position;

        // Prefer the pair the playhead sits between.
        for (int i = 0; i < _markers.Count - 1; i++)
        {
            if (pos >= _markers[i] && pos <= _markers[i + 1])
            {
                return (_markers[i], _markers[i + 1]);
            }
        }

        // Playhead is outside every pair (before the first / after the last) — use the nearest end pair.
        return pos < _markers[0]
            ? (_markers[0], _markers[1])
            : (_markers[^2], _markers[^1]);
    }

    private void UpdateSelectionState()
    {
        var selection = CurrentSelection();
        bool has = selection is not null;

        ExportButton.IsEnabled = has;
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

        double x = e.GetPosition(Timeline).X;
        double fraction = Math.Clamp(x / Timeline.ActualWidth, 0, 1);
        Seek(TimeSpan.FromTicks((long)(_duration.Ticks * fraction)));
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

        TimeText.Text = $"{Format(pos)} / {Format(_duration)}";

        // The active pair can change as the playhead crosses a marker.
        UpdateSelectionState();
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}";

    // --- Actions ---

    private void GrabFrame_Click(object sender, RoutedEventArgs e)
    {
        var folder = App.Settings.EnsureCaptureFolder();
        RunFfmpegAction(
            () => VideoEditor.GrabFrame(_sourcePath, Player.Position.TotalSeconds, folder),
            result =>
            {
                // Open the saved frame in the screenshot editor — the same annotate/save flow.
                PreviewWindow.FromFile(result.OutputPath!).Show();
            });
    }

    private void ExportSelection_Click(object sender, RoutedEventArgs e)
    {
        var selection = CurrentSelection();
        if (selection is null)
        {
            return;
        }

        RunFfmpegAction(
            () => VideoEditor.ExportSpan(_sourcePath, selection.Value.Start.TotalSeconds, selection.Value.End.TotalSeconds),
            result => NotifyAndRefreshGallery(result.OutputPath!));
    }

    private void RemoveSelection_Click(object sender, RoutedEventArgs e)
    {
        var selection = CurrentSelection();
        if (selection is null)
        {
            return;
        }

        RunFfmpegAction(
            () => VideoEditor.RemoveSpan(_sourcePath, selection.Value.Start.TotalSeconds, selection.Value.End.TotalSeconds),
            result => NotifyAndRefreshGallery(result.OutputPath!));
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
        ExportButton.IsEnabled = !busy && CurrentSelection() is not null;
        RemoveButton.IsEnabled = !busy && CurrentSelection() is not null;
        FrameButton.IsEnabled = !busy;
        MarkerButton.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private static void NotifyAndRefreshGallery(string producedPath)
    {
        // Surface the new clip in the docked gallery, selected — same feedback as a fresh capture.
        ((App)System.Windows.Application.Current).ShowGalleryDocked(producedPath);
    }

    // --- Keyboard ---

    protected override void OnKeyDown(KeyEventArgs e)
    {
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

    protected override void OnClosed(EventArgs e)
    {
        _playheadTimer.Stop();
        Player.Close();
        base.OnClosed(e);
    }
}
