using System;
using MediaBrowser.Model.Configuration;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Builds a <see cref="TranscodePlan"/> by delegating to <see cref="EncodingHelper"/>'s existing
/// encoder-selection logic, for diagnostic logging only (transcoding-pipeline plan PR7). This is
/// deliberately a thin wrapper, not an independent re-derivation: the decision must match
/// <see cref="EncodingHelper.GetVideoEncoder"/> by construction, since that method is what
/// actually drives the ffmpeg command today. Auto-selection driven by the plan is PR8.
/// </summary>
public static class TranscodePlanner
{
    /// <summary>
    /// Creates a <see cref="TranscodePlan"/> describing the video-encoder decision
    /// <paramref name="encodingHelper"/> would make for <paramref name="state"/>.
    /// </summary>
    /// <param name="encodingHelper">The encoding helper whose selection logic is being reported.</param>
    /// <param name="state">The job state to plan for.</param>
    /// <param name="encodingOptions">The server's encoding options.</param>
    /// <returns>The resulting plan.</returns>
    public static TranscodePlan CreatePlan(EncodingHelper encodingHelper, EncodingJobInfo state, EncodingOptions encodingOptions)
    {
        ArgumentNullException.ThrowIfNull(encodingHelper);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(encodingOptions);

        var selectedVideoEncoder = encodingHelper.GetVideoEncoder(state, encodingOptions);

        return new TranscodePlan(
            state.OutputVideoCodec,
            encodingOptions.HardwareAccelerationType,
            selectedVideoEncoder,
            selectedVideoEncoder.Contains('_', StringComparison.Ordinal));
    }
}
