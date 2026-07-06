using Reefin.Model.Entities;

namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// Decides whether a failed transcode attempt's hardware acceleration backend should be retried
/// in software, given the <see cref="FfmpegErrorCategory"/> <see cref="FfmpegErrorClassifier"/>
/// assigned to its stderr (transcoding-pipeline plan PR9).
/// </summary>
/// <remarks>
/// This is deliberately the decision logic only, not wired into <c>TranscodeManager</c> or any
/// live retry path - the same scoping <c>FfmpegProgressParser</c> (PR3) and <c>TranscodePlanner</c>
/// (PR7) used for pieces that could not be verified end-to-end here. Real multi-attempt fallback
/// needs two things this environment cannot provide: a way to actually trigger the runtime
/// hardware failures being classified (the working VAAPI device verified in PR8 cannot be made to
/// fail on demand), and a live playback client to confirm a mid-session retry does not break
/// stream continuity (HLS timestamps, manifest state). Wiring this in also means rebuilding the
/// ffmpeg command with a different <see cref="HardwareAccelerationType"/> from where it is
/// actually assembled - the API controller layer (<c>DynamicHlsController</c> and friends) via
/// <see cref="EncodingHelper"/> - not from <c>TranscodeManager.StartFfMpeg</c>, which only
/// receives the already-built command string. None of that is built here.
/// </remarks>
public static class TranscodeFallbackPlanner
{
    /// <summary>
    /// Decides whether to fall back to software encoding for a failed attempt.
    /// </summary>
    /// <param name="failureCategory">The category <see cref="FfmpegErrorClassifier"/> assigned to the failed attempt's stderr.</param>
    /// <param name="currentHardwareAccelerationType">The hardware acceleration backend the failed attempt used.</param>
    /// <returns>The fallback decision.</returns>
    public static TranscodeFallbackDecision Evaluate(FfmpegErrorCategory failureCategory, HardwareAccelerationType currentHardwareAccelerationType)
    {
        // Already software - there is nothing left to fall back to.
        if (currentHardwareAccelerationType == HardwareAccelerationType.none)
        {
            return new TranscodeFallbackDecision(false, HardwareAccelerationType.none);
        }

        // Only categories where "the hardware backend specifically can't do this" is a plausible
        // read of the failure. InvalidInput and PermissionDenied are about the input file, not
        // the encoder - retrying with a different backend would fail the same way. Unknown is not
        // retried because guessing the cause would be exactly the kind of untested assumption this
        // plan has avoided throughout.
        var shouldFallback = failureCategory is FfmpegErrorCategory.DeviceInitializationFailed or FfmpegErrorCategory.UnsupportedCodec;

        return new TranscodeFallbackDecision(shouldFallback, HardwareAccelerationType.none);
    }
}
