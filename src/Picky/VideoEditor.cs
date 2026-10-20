using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Picky;

/// <summary>
/// ffmpeg operations for the video editor: render a list of kept blocks into one clip, and grab a
/// single frame as a PNG. All times are source times.
/// </summary>
internal static class VideoEditor
{
    /// <summary>Outcome of an operation: the produced file on success, or an error message.</summary>
    internal sealed record Result(bool Ok, string? OutputPath, string? Error)
    {
        public static Result Success(string path) => new(true, path, null);
        public static Result Fail(string message) => new(false, null, message);
    }

    /// <summary>Generous: a long clip re-encodes in real time or slower on a laptop.</summary>
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Renders <paramref name="segments"/> back to back into <paramref name="outputPath"/>. Each
    /// block is trimmed out of the source and the blocks are joined with the concat filter, which
    /// gives clean joins regardless of keyframes. Audio is carried along when the source has it.
    /// </summary>
    public static Result Render(string sourcePath, IReadOnlyList<Segment> segments, string outputPath)
    {
        if (!File.Exists(sourcePath))
        {
            return Result.Fail("The clip no longer exists.");
        }
        if (segments.Count == 0)
        {
            return Result.Fail("Nothing to render.");
        }

        bool audio = HasAudio(sourcePath);
        var filter = new StringBuilder();
        var inputs = new StringBuilder();

        for (int i = 0; i < segments.Count; i++)
        {
            var a = Fmt(segments[i].Start.TotalSeconds);
            var b = Fmt(segments[i].End.TotalSeconds);
            filter.Append($"[0:v]trim=start={a}:end={b},setpts=PTS-STARTPTS[v{i}];");
            inputs.Append($"[v{i}]");
            if (audio)
            {
                filter.Append($"[0:a]atrim=start={a}:end={b},asetpts=PTS-STARTPTS[a{i}];");
                inputs.Append($"[a{i}]");
            }
        }

        filter.Append(audio
            ? $"{inputs}concat=n={segments.Count}:v=1:a=1[outv][outa]"
            : $"{inputs}concat=n={segments.Count}:v=1:a=0[outv]");

        var map = audio ? "-map \"[outv]\" -map \"[outa]\" -c:a aac -b:a 160k" : "-map \"[outv]\"";

        if (Run($"-y -i \"{sourcePath}\" -filter_complex \"{filter}\" {map} " +
                $"-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart \"{outputPath}\"",
                RenderTimeout).Ok
            && IsNonEmpty(outputPath))
        {
            return Result.Success(outputPath);
        }

        SafeDelete(outputPath);
        return Result.Fail("ffmpeg could not render the edit.");
    }

    /// <summary>Extracts the frame at <paramref name="atSeconds"/> as a PNG in <paramref name="outputFolder"/>.</summary>
    public static Result GrabFrame(string sourcePath, double atSeconds, string outputFolder)
    {
        if (!File.Exists(sourcePath))
        {
            return Result.Fail("The clip no longer exists.");
        }

        Directory.CreateDirectory(outputFolder);
        var output = Path.Combine(outputFolder, $"Picky_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        if (Run($"-y -ss {Fmt(Math.Max(0, atSeconds))} -i \"{sourcePath}\" -frames:v 1 \"{output}\"",
                TimeSpan.FromMinutes(1)).Ok
            && IsNonEmpty(output))
        {
            return Result.Success(output);
        }

        SafeDelete(output);
        return Result.Fail("ffmpeg could not capture the current frame.");
    }

    /// <summary>Clip duration in seconds (0 if it can't be read).</summary>
    public static double Probe(string sourcePath)
        => VideoThumbnailer.Probe(sourcePath).Duration.TotalSeconds;

    /// <summary>A free path in <paramref name="folder"/> named after the source plus a tag.</summary>
    public static string FreePath(string folder, string sourcePath, string tag, string extension)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = Path.Combine(folder, $"{stem}_{tag}{extension}");
        for (int n = 2; File.Exists(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{stem}_{tag}{n}{extension}");
        }
        return candidate;
    }

    // --- internals ---

    /// <summary>ffmpeg prints the stream list to stderr for <c>-i</c> with no output.</summary>
    private static bool HasAudio(string sourcePath)
        => Run($"-i \"{sourcePath}\"", TimeSpan.FromSeconds(15), quiet: false).Stderr.Contains("Audio:");

    private static string Fmt(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool IsNonEmpty(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private static void SafeDelete(string path)
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
            // Best-effort cleanup of a failed output.
        }
    }

    /// <summary>Runs ffmpeg, draining both pipes. Ok is true only on a clean exit within the timeout.</summary>
    private static (bool Ok, string Stderr) Run(string arguments, TimeSpan timeout, bool quiet = true)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = RecordingController.FindFfmpeg(),
                Arguments = (quiet ? "-hide_banner -loglevel error " : "-hide_banner ") + arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, string.Empty);
            }

            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (false, string.Empty);
            }

            var err = stderr.GetAwaiter().GetResult();
            stdout.GetAwaiter().GetResult();
            return (process.ExitCode == 0, err);
        }
        catch
        {
            return (false, string.Empty);
        }
    }
}
