using System;
using System.Globalization;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Accumulates ffmpeg's <c>-progress pipe:1</c> key=value lines into an
/// <see cref="FfmpegProgressUpdate"/> per block, replacing the old approach of regex-splitting
/// free-form human progress stats out of stderr (<c>fps=</c>/<c>time=</c>/<c>size=</c>/
/// <c>bitrate=</c>) - a format never intended as a stable API. This parser targets the field
/// names confirmed against real ffmpeg 6.1.1 <c>-progress pipe:1 -nostats</c> output.
/// </summary>
/// <remarks>
/// Not yet wired into the live transcode path: doing so requires adding
/// <c>-progress pipe:1 -nostats</c> to every command-line builder in <c>EncodingHelper</c> and
/// redirecting+draining ffmpeg's stdout in <c>TranscodeManager.StartFfMpeg</c> (stdout is
/// confirmed unused by every job type today - no builder ever targets <c>pipe:1</c> or <c>-</c>
/// for output - but making that switch is a separate, larger change than this parser itself).
/// </remarks>
public sealed class FfmpegProgressParser
{
    private long? _frame;
    private float? _fps;
    private long? _totalSizeBytes;
    private long? _outTimeUs;
    private float? _speed;

    /// <summary>
    /// Feeds one line of <c>-progress pipe:1</c> output into the parser.
    /// </summary>
    /// <param name="line">A single <c>key=value</c> line.</param>
    /// <returns>
    /// The accumulated <see cref="FfmpegProgressUpdate"/> once a <c>progress=</c> line completes
    /// the current block, otherwise null.
    /// </returns>
    public FfmpegProgressUpdate? ConsumeLine(ReadOnlySpan<char> line)
    {
        var separatorIndex = line.IndexOf('=');
        if (separatorIndex < 0)
        {
            return null;
        }

        var key = line[..separatorIndex];
        var value = line[(separatorIndex + 1)..].Trim();

        if (key.Equals("frame", StringComparison.Ordinal))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
            {
                _frame = frame;
            }
        }
        else if (key.Equals("fps", StringComparison.Ordinal))
        {
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
            {
                _fps = fps;
            }
        }
        else if (key.Equals("total_size", StringComparison.Ordinal))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalSize))
            {
                _totalSizeBytes = totalSize;
            }
        }
        else if (key.Equals("out_time_us", StringComparison.Ordinal))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var outTimeUs))
            {
                _outTimeUs = outTimeUs;
            }
        }
        else if (key.Equals("speed", StringComparison.Ordinal))
        {
            var trimmed = value.EndsWith('x') ? value[..^1].Trim() : value;
            if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            {
                _speed = speed;
            }
        }
        else if (key.Equals("progress", StringComparison.Ordinal))
        {
            var update = new FfmpegProgressUpdate(_frame, _fps, _totalSizeBytes, _outTimeUs, _speed, value.Equals("end", StringComparison.Ordinal));
            _frame = null;
            _fps = null;
            _totalSizeBytes = null;
            _outTimeUs = null;
            _speed = null;
            return update;
        }

        return null;
    }
}
