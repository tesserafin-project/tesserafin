namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// One structured progress snapshot decoded from ffmpeg's <c>-progress pipe:1</c> key=value
/// output, as emitted at each <c>progress=continue</c>/<c>progress=end</c> boundary. Fields are
/// null when ffmpeg reports them as unknown (e.g. "N/A") rather than a parseable number.
/// </summary>
/// <param name="FrameCount">Number of frames encoded so far.</param>
/// <param name="Fps">Current encoding frame rate.</param>
/// <param name="TotalSizeBytes">Output size in bytes so far.</param>
/// <param name="OutTimeMicroseconds">Output timestamp in microseconds - the exact-position counterpart to the old fragile <c>time=</c> stderr field.</param>
/// <param name="Speed">Encoding speed as a multiple of realtime (e.g. 2.5 for "2.5x").</param>
/// <param name="IsEnd">Whether this snapshot was terminated by <c>progress=end</c> rather than <c>progress=continue</c>.</param>
public sealed record FfmpegProgressUpdate(
    long? FrameCount,
    float? Fps,
    long? TotalSizeBytes,
    long? OutTimeMicroseconds,
    float? Speed,
    bool IsEnd);
