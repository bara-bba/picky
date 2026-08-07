using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace Picky;

/// <summary>
/// Records a screen region to a small H.264 MP4 by driving ffmpeg (gdigrab).
/// </summary>
internal sealed class RecordingController
{
    private Process? _process;

    public bool IsRecording => _process is { HasExited: false };

    public string? OutputPath { get; private set; }

    /// <summary>
    /// The rect actually being captured, in physical pixels. Not necessarily the rect passed to
    /// <see cref="Start"/>: H.264 needs even dimensions, so width/height may each be 1px smaller.
    /// The on-screen recording frame uses this so it lines up with the real capture area.
    /// </summary>
    public Rectangle Region { get; private set; }

    /// <summary>Resolves ffmpeg: %APPDATA%\Picky, next to the app, else "ffmpeg" from PATH.</summary>
    public static string FindFfmpeg()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Picky", "ffmpeg.exe");
        if (File.Exists(appData))
        {
            return appData;
        }

        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        return File.Exists(local) ? local : "ffmpeg";
    }

    /// <summary>Whether ffmpeg can actually be launched (installed / on PATH).</summary>
    public static bool IsAvailable()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(FindFfmpeg(), "-version")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (probe is null)
            {
                return false;
            }
            probe.WaitForExit(3000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Start(Rectangle region, string outputPath, out string? error)
    {
        error = null;

        // H.264 (yuv420p) needs even dimensions.
        int w = region.Width - (region.Width % 2);
        int h = region.Height - (region.Height % 2);
        if (w < 2 || h < 2)
        {
            error = "The region is too small to record.";
            return false;
        }

        // Small files: 15 fps, CRF 30, faststart. veryfast keeps encoding real-time.
        var args =
            $"-y -f gdigrab -framerate 15 -offset_x {region.X} -offset_y {region.Y} " +
            $"-video_size {w}x{h} -i desktop " +
            $"-c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -movflags +faststart " +
            $"\"{outputPath}\"";

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FindFfmpeg(),
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            // Drain the pipes so ffmpeg doesn't block on a full buffer.
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            OutputPath = outputPath;
            Region = new Rectangle(region.X, region.Y, w, h);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _process = null;
            return false;
        }
    }

    /// <summary>Stops recording gracefully (so the MP4 is finalized) and returns the file path.</summary>
    public string? Stop()
    {
        if (_process is null)
        {
            return null;
        }

        try
        {
            if (!_process.HasExited)
            {
                // 'q' tells ffmpeg to finish and write a valid MP4 trailer.
                _process.StandardInput.Write("q");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(8000))
                {
                    _process.Kill();
                }
            }
        }
        catch
        {
            try { _process.Kill(); } catch { /* already gone */ }
        }

        var path = OutputPath;
        _process.Dispose();
        _process = null;
        return path;
    }
}
