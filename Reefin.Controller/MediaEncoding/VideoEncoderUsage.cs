namespace Reefin.Controller.MediaEncoding;

/// <summary>
/// What the encoder selected for an <see cref="EncodingJobInfo"/> will be used for. The two usages
/// do not share an argument generator, so they cannot share the same set of acceptable encoders.
/// </summary>
public enum VideoEncoderUsage
{
    /// <summary>
    /// A streaming or progressive transcode job, whose arguments are produced by
    /// <see cref="EncodingHelper.GetProgressiveVideoArguments"/> and friends, including the
    /// rate-control arguments of <c>GetVideoBitrateParam</c>.
    /// </summary>
    Transcode,

    /// <summary>
    /// Still-image extraction (thumbnails, trickplay), whose arguments are produced by
    /// <c>MediaEncoder.ExtractVideoImagesOnIntervalInternal</c> and are driven by an explicit,
    /// validated quality scale rather than by a bitrate.
    /// </summary>
    ImageExtraction,
}
