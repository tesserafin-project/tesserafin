using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// One ffmpeg process invocation within a <see cref="TranscodingJob"/>. A job survives across
/// attempts (throttling, segment cleanup, download progress, kill timers); the OS process and
/// its exit state belong to whichever attempt is currently running.
/// </summary>
/// <remarks>
/// Today a <see cref="TranscodingJob"/> only ever has one attempt - nothing constructs a second
/// one after a failure. This type exists so that seam is a real object boundary instead of a
/// future rename, not because multi-attempt fallback is implemented or verified here.
/// </remarks>
public sealed class TranscodeAttempt : IDisposable
{
    /// <summary>
    /// Gets or sets the ffmpeg process for this attempt.
    /// </summary>
    public Process? Process { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the process has exited.
    /// </summary>
    public bool HasExited { get; set; }

    /// <summary>
    /// Gets or sets the process exit code.
    /// </summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// Requests a graceful stop by writing "q" to the process's stdin (ffmpeg's own
    /// stop-and-finalize-output handling), falling back to <see cref="Process.Kill()"/> if it
    /// hasn't exited within 5 seconds.
    /// </summary>
    /// <param name="logger">Logger for the stop/kill messages.</param>
    /// <param name="path">Output path, for logging only.</param>
    public void Stop(ILogger logger, string? path)
    {
        var process = Process;
        if (process is null || HasExited)
        {
            return;
        }

        try
        {
            logger.LogInformation("Stopping ffmpeg process with q command for {Path}", path);

            process.StandardInput.WriteLine("q");

            // Need to wait because killing is asynchronous.
            if (!process.WaitForExit(5000))
            {
                logger.LogInformation("Killing FFmpeg process for {Path}", path);
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Process?.Dispose();
        Process = null;
    }
}
