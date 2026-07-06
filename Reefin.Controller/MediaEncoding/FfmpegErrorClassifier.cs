using System;

namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// Classifies a single line of ffmpeg stderr output into a <see cref="FfmpegErrorCategory"/>.
/// </summary>
/// <remarks>
/// Patterns here are matched against real ffmpeg 6.1 output captured by deliberately triggering
/// each failure (bad input path, unwritable output, unknown encoder, bad VAAPI render node) -
/// they are not guessed from documentation. Categories for GPU-specific failure modes that need
/// real hardware to observe (encoder session limits, unsupported profiles, filter init failures,
/// resource exhaustion) are intentionally not implemented yet; adding them without a real sample
/// to match against would just be an untested guess.
/// </remarks>
public static class FfmpegErrorClassifier
{
    /// <summary>
    /// Classifies a single line of ffmpeg stderr output.
    /// </summary>
    /// <param name="line">One line of stderr output.</param>
    /// <returns>The matched category, or <see cref="FfmpegErrorCategory.Unknown"/> if nothing matched.</returns>
    public static FfmpegErrorCategory Classify(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
        {
            return FfmpegErrorCategory.Unknown;
        }

        if (Contains(line, "Error opening input file") || Contains(line, "Error opening input:"))
        {
            return FfmpegErrorCategory.InvalidInput;
        }

        if (Contains(line, "Permission denied"))
        {
            return FfmpegErrorCategory.PermissionDenied;
        }

        if (Contains(line, "Unknown encoder") || Contains(line, "Unknown decoder") || Contains(line, "Encoder not found") || Contains(line, "Decoder not found"))
        {
            return FfmpegErrorCategory.UnsupportedCodec;
        }

        if (Contains(line, "No VA display found")
            || Contains(line, "Device creation failed")
            || Contains(line, "Cannot open the hw device")
            || Contains(line, "Error creating a CUDA context")
            || (Contains(line, "init_hw_device") && Contains(line, "Failed to set value")))
        {
            return FfmpegErrorCategory.DeviceInitializationFailed;
        }

        return FfmpegErrorCategory.Unknown;
    }

    private static bool Contains(ReadOnlySpan<char> line, string pattern)
        => line.Contains(pattern, StringComparison.Ordinal);
}
