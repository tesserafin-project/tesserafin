using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// A diagnostic snapshot of the video-encoder decision <see cref="EncodingHelper"/> made (or would
/// make) for a given <see cref="EncodingJobInfo"/>/<see cref="EncodingOptions"/> pair. Produced by
/// <see cref="TranscodePlanner"/> for logging only - nothing reads this back into the actual
/// transcode command yet. That wiring, and any auto-selection built on top of it, is the next
/// plan phase (PR8), not this one.
/// </summary>
/// <param name="VideoCodec">The requested output video codec, as set on <see cref="EncodingJobInfo.OutputVideoCodec"/> (for example <c>"h264"</c>), or <c>null</c>/empty for a stream copy.</param>
/// <param name="RequestedHardwareAccelerationType">The hardware acceleration backend configured in <see cref="EncodingOptions"/>, regardless of whether it ended up being used.</param>
/// <param name="SelectedVideoEncoder">The concrete ffmpeg encoder name <see cref="EncodingHelper.GetVideoEncoder"/> selected, for example <c>"h264_vaapi"</c>, <c>"libx264"</c>, or <c>"copy"</c>.</param>
/// <param name="IsHardwareEncoder">Whether <see cref="SelectedVideoEncoder"/> is a hardware-backed encoder rather than a software one or a stream copy.</param>
public sealed record TranscodePlan(
    string? VideoCodec,
    HardwareAccelerationType RequestedHardwareAccelerationType,
    string SelectedVideoEncoder,
    bool IsHardwareEncoder);
