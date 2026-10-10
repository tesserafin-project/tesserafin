using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Model.Entities;

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
    private volatile bool _stopRequested;

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
    /// Gets or sets a value indicating whether this process's standard input carries media bytes
    /// rather than ffmpeg's keyboard commands.
    /// </summary>
    /// <remarks>
    /// ffmpeg only interprets stdin as a keyboard when stdin is not one of its inputs. For a job
    /// reading <c>-i pipe:0</c>, writing "q" would be muxed into the media as garbage instead of
    /// stopping anything, so such a job is stopped by closing the pipe (see
    /// <c>TranscodingJob.Stop</c>) and then killing the process.
    /// </remarks>
    public bool StandardInputIsMediaPipe { get; set; }

    /// <summary>
    /// Gets a value indicating whether the server asked this process to stop. A process that ends
    /// after that did not fail, whatever its exit code says.
    /// </summary>
    public bool StopRequested => _stopRequested;

    /// <summary>
    /// Gets or sets the hardware backend this attempt's command was built for.
    /// </summary>
    public HardwareAccelerationType Backend { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the command that ran used a hardware device.
    /// </summary>
    public bool UsesHardwarePipeline { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this ffmpeg has the software encoder for the same output.
    /// </summary>
    public bool SoftwareAlternativeAvailable { get; set; }

    /// <summary>
    /// Gets or sets the reader of this attempt's stderr, which holds what it recognised.
    /// </summary>
    public JobLogger? Diagnostics { get; set; }

    /// <summary>
    /// Gets or sets the task that completes when stderr has been read to its end.
    /// </summary>
    public Task? DiagnosticsCompleted { get; set; }

    /// <summary>
    /// Records that the server is stopping this attempt. Called before anything that could make
    /// the process exit, so that exit is never judged as a failure.
    /// </summary>
    public void MarkStopRequested() => _stopRequested = true;

    /// <summary>
    /// Requests a graceful stop, falling back to <see cref="Process.Kill()"/> if the process
    /// hasn't exited within 5 seconds. For an ordinary job the graceful stop is writing "q" to the
    /// process's stdin (ffmpeg's own stop-and-finalize-output handling); for a job whose stdin is
    /// a media pipe it is the caller closing that pipe before calling this.
    /// </summary>
    /// <param name="logger">Logger for the stop/kill messages.</param>
    /// <param name="path">Output path, for logging only.</param>
    public void Stop(ILogger logger, string? path)
    {
        // Set before anything else, and whether or not the process is still there: the exit
        // handler reads it to tell a stop from a failure, and the two can race.
        _stopRequested = true;

        var process = Process;
        if (process is null || HasExited)
        {
            return;
        }

        try
        {
            if (StandardInputIsMediaPipe)
            {
                logger.LogInformation("Stopping ffmpeg process fed from a media pipe for {Path}", path);
            }
            else
            {
                logger.LogInformation("Stopping ffmpeg process with q command for {Path}", path);

                try
                {
                    process.StandardInput.WriteLine("q");
                }
                catch (IOException)
                {
                    // Nobody is reading: the process has exited and its exit is still being
                    // handled, so HasExited does not say so yet (tesserafin#289) - or it is alive
                    // and deaf, and the wait below ends in a kill.
                }
            }

            // Need to wait because killing is asynchronous.
            if (!process.WaitForExit(5000))
            {
                logger.LogInformation("Killing FFmpeg process for {Path}", path);
                process.Kill();

                // Killing is asynchronous too, and a caller of this is told the process has stopped.
                process.WaitForExit(5000);
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
