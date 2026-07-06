using System;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Runs short-lived ffmpeg/ffprobe probe invocations (codec/filter/hwaccel detection, version
/// checks) behind a single, testable component. Unlike ad-hoc <see cref="System.Diagnostics.Process"/>
/// usage, implementations must drain both stdout and stderr concurrently - reading only one can
/// deadlock once the unread stream's OS pipe buffer fills.
/// </summary>
public interface IFfmpegProcessRunner
{
    /// <summary>
    /// Starts <paramref name="command"/>, drains stdout and stderr concurrently, and waits for
    /// exit or <paramref name="timeout"/>, whichever comes first.
    /// </summary>
    /// <param name="command">The command to run.</param>
    /// <param name="timeout">Maximum time to wait before killing the process and returning with <see cref="FfmpegProcessResult.TimedOut"/> set.</param>
    /// <param name="cancellationToken">
    /// External cancellation. Unlike <paramref name="timeout"/>, cancelling this token kills the
    /// process and throws <see cref="OperationCanceledException"/>, per standard async cancellation.
    /// </param>
    /// <param name="standardInput">Text to write to the process's standard input and close, or null to leave stdin unredirected.</param>
    /// <returns>The process outcome.</returns>
    Task<FfmpegProcessResult> RunProbeAsync(FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null);
}
