using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Picky;

/// <summary>
/// Pulls a poster frame and the duration out of a recorded clip using the bundled ffmpeg.
///
/// Results are cached under <c>%LocalAppData%\Picky\thumbnails</c>. That location is
/// deliberately *outside* the capture folder: the gallery enumerates the capture folder, so
/// thumbnails written next to the clips would show up as captures in their own right.
/// </summary>
internal static class VideoThumbnailer
{
    internal sealed record Info(string? ThumbnailPath, TimeSpan Duration);

    /// <summary>ffmpeg spawns aren't free — don't let a folder of clips start a process storm.</summary>
    private static readonly SemaphoreSlim Gate = new(2, 2);

    private static string CacheFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Picky",
        "thumbnails");

    /// <summary>Blocking; call from a background thread.</summary>
    public static Info Probe(string videoPath, int width = 480)
    {
        try
        {
            var file = new FileInfo(videoPath);
            if (!file.Exists)
            {
                return new Info(null, TimeSpan.Zero);
            }

            Directory.CreateDirectory(CacheFolder);

            // Keyed on length + write time so an edited or replaced clip re-renders.
            var key = $"{Path.GetFileNameWithoutExtension(videoPath)}-{file.Length}-{file.LastWriteTimeUtc.Ticks:X}";
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                key = key.Replace(invalid, '_');
            }

            var thumbnailPath = Path.Combine(CacheFolder, key + ".png");
            var metaPath = Path.Combine(CacheFolder, key + ".meta");

            // Cache hit needs no ffmpeg at all: the duration is kept in a sidecar.
            if (File.Exists(thumbnailPath) && File.Exists(metaPath)
                && double.TryParse(File.ReadAllText(metaPath), NumberStyles.Float, CultureInfo.InvariantCulture, out var cached))
            {
                return new Info(thumbnailPath, TimeSpan.FromSeconds(cached));
            }

            Gate.Wait();
            try
            {
                var duration = ReadDuration(videoPath);

                // Seek slightly in — frame 0 of a screen recording is often a blank first paint.
                double seek = duration.TotalSeconds > 2 ? Math.Min(1.0, duration.TotalSeconds * 0.1) : 0;

                if (Extract(videoPath, thumbnailPath, seek, width))
                {
                    File.WriteAllText(metaPath, duration.TotalSeconds.ToString("R", CultureInfo.InvariantCulture));
                    return new Info(thumbnailPath, duration);
                }

                return new Info(null, duration);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch
        {
            // A thumbnail is a nicety; never let it break the gallery.
            return new Info(null, TimeSpan.Zero);
        }
    }

    private static TimeSpan ReadDuration(string videoPath)
    {
        var output = RunFfmpeg($"-hide_banner -i \"{videoPath}\"");
        var match = Regex.Match(output, @"Duration:\s*(\d+):(\d{2}):(\d{2})\.(\d+)");

        if (!match.Success)
        {
            return TimeSpan.Zero;
        }

        int hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        double fraction = double.Parse("0." + match.Groups[4].Value, CultureInfo.InvariantCulture);

        return new TimeSpan(0, hours, minutes, seconds, (int)(fraction * 1000));
    }

    private static bool Extract(string videoPath, string outputPath, double seekSeconds, int width)
    {
        // scale=W:-2 preserves aspect ratio and keeps the height even.
        var seek = seekSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        RunFfmpeg($"-hide_banner -y -ss {seek} -i \"{videoPath}\" -frames:v 1 -vf \"scale={width}:-2\" \"{outputPath}\"");

        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    /// <summary>Runs ffmpeg and returns its combined output (ffmpeg reports to stderr).</summary>
    private static string RunFfmpeg(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = RecordingController.FindFfmpeg(),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return string.Empty;
        }

        // Drain both pipes concurrently so ffmpeg can't block on a full buffer.
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();

        if (!process.WaitForExit(15000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
        }

        return stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult();
    }
}
