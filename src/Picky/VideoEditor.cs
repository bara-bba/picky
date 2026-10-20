using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Picky;

/// <summary>
/// Video edits driven by the bundled ffmpeg: trim a span out to a clip, cut a span away (keep
/// everything else), and grab a single frame as a PNG.
///
/// <para>The span operations write to a caller-supplied output path (the editor hands in a temp
/// working copy, never the original) and report the path they produced. All timing is in seconds
/// from the start of the clip.</para>
/// </summary>
internal static class VideoEditor
{
    /// <summary>Outcome of an edit: the produced file on success, or an error message.</summary>
    internal sealed record Result(bool Ok, string? OutputPath, string? Error)
    {
        public static Result Success(string path) => new(true, path, null);
        public static Result Fail(string message) => new(false, null, message);
    }

    /// <summary>
    /// Exports the span <paramref name="startSeconds"/>..<paramref name="endSeconds"/> to
    /// <paramref name="outputPath"/>. Tries a lossless stream copy first; if that produces an empty
    /// or unplayable file (keyframe misalignment can do this), re-encodes so the result is valid.
    /// </summary>
    public static Result ExportSpanTo(string sourcePath, double startSeconds, double endSeconds, string outputPath)
    {
        if (!TryValidateSpan(sourcePath, startSeconds, endSeconds, out var error))
        {
            return Result.Fail(error);
        }

        double duration = endSeconds - startSeconds;
        var ss = Fmt(startSeconds);
        var t = Fmt(duration);

        // Accurate seek (-ss after -i) + stream copy keeps it lossless and fast.
        if (Run($"-y -i \"{sourcePath}\" -ss {ss} -t {t} -c copy -movflags +faststart \"{outputPath}\"")
            && IsNonEmpty(outputPath))
        {
            return Result.Success(outputPath);
        }

        // Copy failed or produced nothing — re-encode (slower, but always valid).
        if (Run($"-y -ss {ss} -i \"{sourcePath}\" -t {t} -c:v libx264 -preset veryfast -crf 23 " +
                $"-pix_fmt yuv420p -movflags +faststart \"{outputPath}\"")
            && IsNonEmpty(outputPath))
        {
            return Result.Success(outputPath);
        }

        SafeDelete(outputPath);
        return Result.Fail("ffmpeg could not export the selected span.");
    }

    /// <summary>
    /// Removes the span <paramref name="startSeconds"/>..<paramref name="endSeconds"/> and keeps the
    /// rest, writing to <paramref name="outputPath"/>. The two surviving pieces (<c>[0,start]</c>
    /// and <c>[end,eof]</c>) are re-encoded and concatenated through the filtergraph so the join is
    /// clean regardless of keyframes. A span that touches an end degenerates to a plain trim.
    /// </summary>
    public static Result RemoveSpanTo(string sourcePath, double startSeconds, double endSeconds, string outputPath)
    {
        if (!TryValidateSpan(sourcePath, startSeconds, endSeconds, out var error))
        {
            return Result.Fail(error);
        }

        var total = Probe(sourcePath);
        if (total <= 0)
        {
            return Result.Fail("Couldn't read the clip's duration.");
        }

        const double epsilon = 0.05;
        bool keepHead = startSeconds > epsilon;
        bool keepTail = endSeconds < total - epsilon;

        if (!keepHead && !keepTail)
        {
            return Result.Fail("That would remove the entire clip.");
        }

        // Only one side survives — it's just a trim of the remaining piece.
        if (keepHead && !keepTail)
        {
            return TrimTo(sourcePath, outputPath, 0, startSeconds);
        }
        if (!keepHead && keepTail)
        {
            return TrimTo(sourcePath, outputPath, endSeconds, total);
        }

        // Both sides survive: encode [0,start] + [end,total] and concat via the filtergraph.
        var a0 = Fmt(0);
        var a1 = Fmt(startSeconds);
        var b0 = Fmt(endSeconds);
        var b1 = Fmt(total);

        var filter =
            $"[0:v]trim=start={a0}:end={a1},setpts=PTS-STARTPTS[v0];" +
            $"[0:v]trim=start={b0}:end={b1},setpts=PTS-STARTPTS[v1];" +
            $"[v0][v1]concat=n=2:v=1:a=0[outv]";

        if (Run($"-y -i \"{sourcePath}\" -filter_complex \"{filter}\" -map \"[outv]\" " +
                $"-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -movflags +faststart \"{outputPath}\"")
            && IsNonEmpty(outputPath))
        {
            return Result.Success(outputPath);
        }

        SafeDelete(outputPath);
        return Result.Fail("ffmpeg could not cut the selected span.");
    }

    /// <summary>
    /// Extracts the frame at <paramref name="atSeconds"/> as a PNG in <paramref name="outputFolder"/>
    /// and returns its path, so the caller can hand it to the screenshot editor.
    /// </summary>
    public static Result GrabFrame(string sourcePath, double atSeconds, string outputFolder)
    {
        if (!File.Exists(sourcePath))
        {
            return Result.Fail("The clip no longer exists.");
        }

        Directory.CreateDirectory(outputFolder);
        var output = Path.Combine(outputFolder, $"Picky_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        // Accurate seek + a single frame. -update/-frames:v 1 writes exactly one image.
        if (Run($"-y -ss {Fmt(Math.Max(0, atSeconds))} -i \"{sourcePath}\" -frames:v 1 \"{output}\"")
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

    // --- internals ---

    private static Result TrimTo(string source, string output, double start, double end)
    {
        var ss = Fmt(start);
        var t = Fmt(end - start);

        if (Run($"-y -i \"{source}\" -ss {ss} -t {t} -c copy -movflags +faststart \"{output}\"")
            && IsNonEmpty(output))
        {
            return Result.Success(output);
        }

        if (Run($"-y -ss {ss} -i \"{source}\" -t {t} -c:v libx264 -preset veryfast -crf 23 " +
                $"-pix_fmt yuv420p -movflags +faststart \"{output}\"")
            && IsNonEmpty(output))
        {
            return Result.Success(output);
        }

        SafeDelete(output);
        return Result.Fail("ffmpeg could not produce the trimmed clip.");
    }

    private static bool TryValidateSpan(string sourcePath, double start, double end, out string error)
    {
        if (!File.Exists(sourcePath))
        {
            error = "The clip no longer exists.";
            return false;
        }

        if (end - start < 0.1)
        {
            error = "The selection is too short (set two markers around a span first).";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>ffmpeg wants a '.'-decimal, culture-invariant seconds value.</summary>
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
            // Best-effort cleanup of a failed output; nothing to do if it's locked.
        }
    }

    /// <summary>Runs ffmpeg, draining both pipes; returns true only on a clean exit.</summary>
    private static bool Run(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = RecordingController.FindFfmpeg(),
                Arguments = "-hide_banner -loglevel error " + arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // Drain concurrently so a full stderr buffer can't deadlock ffmpeg.
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return false;
            }

            stderr.GetAwaiter().GetResult();
            stdout.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
