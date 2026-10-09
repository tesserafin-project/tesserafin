using System;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Classifies a single line of ffmpeg stderr output into a <see cref="FfmpegErrorCategory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Patterns here are matched against real ffmpeg output captured by deliberately triggering each
/// failure (bad input path, unwritable output, unknown encoder, bad VAAPI render node) - they are
/// not guessed from documentation. Categories for GPU-specific failure modes that need real
/// hardware to observe (encoder session limits, unsupported profiles, filter init failures,
/// resource exhaustion) are intentionally not implemented yet; adding them without a real sample
/// to match against would just be an untested guess.
/// </para>
/// <para>
/// The run-time hardware patterns were captured from Tesserafin FFmpeg 7.1.4 driving a real
/// AMD VAAPI device (Mesa radeonsi) whose DRM ioctls were made to fail for that one process:
/// <c>Failed to initialise VAAPI connection</c> when the device stops answering before the
/// process opens it, and the two <c>amdgpu: The CS has ...</c> lines when command submission
/// is refused under a running encode. Those two are printed by the driver, not by ffmpeg, and
/// are AMD-specific; no other vendor's equivalent is claimed. A process that merely dies - a
/// signal, a segmentation fault, a bare non-zero exit code - matches nothing here on purpose.
/// </para>
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

        if (Contains(line, "amdgpu: The CS has cancelled because the context is lost")
            || Contains(line, "amdgpu: The CS has been rejected"))
        {
            return FfmpegErrorCategory.HardwareDeviceLost;
        }

        if (Contains(line, "No VA display found")
            || Contains(line, "Failed to initialise VAAPI connection")
            || Contains(line, "Device creation failed")
            || Contains(line, "Cannot open the hw device")
            || Contains(line, "Error creating a CUDA context")
            || (Contains(line, "init_hw_device") && Contains(line, "Failed to set value")))
        {
            return FfmpegErrorCategory.DeviceInitializationFailed;
        }

        return FfmpegErrorCategory.Unknown;
    }

    /// <summary>
    /// Extracts the codec named by an "Unknown encoder 'x'" / "Unknown decoder 'x'" line.
    /// </summary>
    /// <param name="line">One line of stderr.</param>
    /// <returns>The quoted codec name, or <see langword="null"/> when the line names none.</returns>
    public static string? GetUnsupportedCodecName(ReadOnlySpan<char> line)
    {
        foreach (var marker in new[] { "Unknown encoder '", "Unknown decoder '" })
        {
            var start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var rest = line[(start + marker.Length)..];
            var end = rest.IndexOf('\'');
            if (end > 0)
            {
                return rest[..end].ToString();
            }
        }

        return null;
    }

    private static bool Contains(ReadOnlySpan<char> line, string pattern)
        => line.Contains(pattern, StringComparison.Ordinal);
}
