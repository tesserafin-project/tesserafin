namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// The outcome of a short-lived ffmpeg/ffprobe probe invocation run via <see cref="IFfmpegProcessRunner"/>.
/// </summary>
/// <param name="ExitCode">The process exit code, or null if the process was killed due to timeout.</param>
/// <param name="StandardOutput">The fully-drained standard output.</param>
/// <param name="StandardError">The fully-drained standard error.</param>
/// <param name="TimedOut">Whether the process was killed because it exceeded the requested timeout.</param>
public sealed record FfmpegProcessResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut);
