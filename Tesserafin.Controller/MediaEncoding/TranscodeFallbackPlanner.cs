using System;
using System.Linq;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Decides whether a hardware transcode that failed may be retried in software.
/// </summary>
/// <remarks>
/// <para>
/// The decision is pure: it sees what the attempt ran and which categories
/// <see cref="FfmpegErrorClassifier"/> found in its stderr, and nothing else. It never starts
/// anything. <c>TranscodeManager</c> applies the result when ffmpeg exits (tesserafin#119).
/// </para>
/// <para>
/// A fallback is granted only on positive evidence that the hardware side failed. An exit code,
/// a signal, or stderr in which nothing was recognised is never such evidence.
/// </para>
/// </remarks>
public static class TranscodeFallbackPlanner
{
    /// <summary>
    /// Evaluates one failed attempt.
    /// </summary>
    /// <param name="request">What ran and what its stderr showed.</param>
    /// <returns>The decision.</returns>
    public static TranscodeFallbackDecision Evaluate(TranscodeFallbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.StopRequested)
        {
            return Refuse("the attempt was stopped on request");
        }

        if (request.Backend == HardwareAccelerationType.none || !request.UsedHardwarePipeline)
        {
            return Refuse("the attempt was already software");
        }

        if (request.Categories.Count == 0)
        {
            return Refuse("the failure was not recognised");
        }

        // Evidence about the input outranks everything: software would open the same file.
        if (request.Categories.Contains(FfmpegErrorCategory.InvalidInput)
            || request.Categories.Contains(FfmpegErrorCategory.PermissionDenied))
        {
            return Refuse("the input could not be read");
        }

        if (!request.SoftwareAlternativeAvailable)
        {
            return Refuse("no software encoder is available for this output");
        }

        if (request.Categories.Contains(FfmpegErrorCategory.HardwareDeviceLost))
        {
            return Grant(TranscodeFallbackScope.Backend, "the hardware device stopped accepting work");
        }

        if (request.Categories.Contains(FfmpegErrorCategory.DeviceInitializationFailed))
        {
            return Grant(TranscodeFallbackScope.Backend, "the hardware device could not be opened");
        }

        if (request.Categories.Contains(FfmpegErrorCategory.UnsupportedCodec))
        {
            // "Unknown encoder" also describes a software codec this build lacks, which software
            // cannot fix. Only a codec that belongs to the backend that was in use counts.
            return IsCodecOfBackend(request.UnsupportedCodecName, request.Backend)
                ? Grant(TranscodeFallbackScope.MediaSource, "the hardware codec is not available")
                : Refuse("the missing codec is not a hardware codec of the backend in use");
        }

        return Refuse("the failure was not recognised");
    }

    /// <summary>
    /// Evaluates a failure from its category and backend alone.
    /// </summary>
    /// <remarks>
    /// Kept for callers that have nothing else. Without the command that ran and the codec that
    /// was named it cannot tell a missing hardware codec from a missing software one, so
    /// <see cref="FfmpegErrorCategory.UnsupportedCodec"/> is refused here.
    /// </remarks>
    /// <param name="failureCategory">The category assigned to the failed attempt's stderr.</param>
    /// <param name="currentHardwareAccelerationType">The backend the failed attempt was using.</param>
    /// <returns>The decision.</returns>
    public static TranscodeFallbackDecision Evaluate(FfmpegErrorCategory failureCategory, HardwareAccelerationType currentHardwareAccelerationType)
        => Evaluate(new TranscodeFallbackRequest(
            failureCategory == FfmpegErrorCategory.Unknown ? [] : [failureCategory],
            currentHardwareAccelerationType,
            UsedHardwarePipeline: currentHardwareAccelerationType != HardwareAccelerationType.none,
            SoftwareAlternativeAvailable: true,
            StopRequested: false,
            UnsupportedCodecName: null));

    private static TranscodeFallbackDecision Refuse(string reason)
        => new(false, HardwareAccelerationType.none) { Reason = reason };

    private static TranscodeFallbackDecision Grant(TranscodeFallbackScope scope, string reason)
        => new(true, HardwareAccelerationType.none) { Scope = scope, Reason = reason };

    private static bool IsCodecOfBackend(string? codecName, HardwareAccelerationType backend)
    {
        if (string.IsNullOrEmpty(codecName))
        {
            return false;
        }

        // The suffixes ffmpeg itself gives each backend's encoders and decoders.
        string[] suffixes = backend switch
        {
            HardwareAccelerationType.vaapi => ["_vaapi"],
            HardwareAccelerationType.qsv => ["_qsv"],
            HardwareAccelerationType.nvenc => ["_nvenc", "_cuvid"],
            HardwareAccelerationType.amf => ["_amf"],
            HardwareAccelerationType.v4l2m2m => ["_v4l2m2m"],
            HardwareAccelerationType.videotoolbox => ["_videotoolbox"],
            HardwareAccelerationType.rkmpp => ["_rkmpp"],
            _ => []
        };

        foreach (var suffix in suffixes)
        {
            if (codecName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
