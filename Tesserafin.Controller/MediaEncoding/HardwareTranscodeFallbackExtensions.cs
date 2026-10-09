using Tesserafin.Model.Configuration;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Reaches <see cref="IHardwareTranscodeFallback"/> from the <see cref="ITranscodeManager"/> a
/// caller already holds. A manager that does not implement it knows of no failure: the caller
/// gets <see langword="null"/> and uses the stored configuration.
/// </summary>
public static class HardwareTranscodeFallbackExtensions
{
    /// <summary>
    /// Gets the encoding options a new command must be built with for this media source.
    /// </summary>
    /// <param name="transcodeManager">The transcode manager.</param>
    /// <param name="mediaSourceId">The media source the command is for, when known.</param>
    /// <returns>The options to build with, or <see langword="null"/> when the manager keeps no such state.</returns>
    public static EncodingOptions? GetEffectiveEncodingOptions(this ITranscodeManager transcodeManager, string? mediaSourceId)
        => (transcodeManager as IHardwareTranscodeFallback)?.GetEffectiveEncodingOptions(mediaSourceId);

    /// <summary>
    /// Gets what is known about the failure of the job writing to <paramref name="path"/>.
    /// </summary>
    /// <param name="transcodeManager">The transcode manager.</param>
    /// <param name="path">The job's output path.</param>
    /// <param name="type">The job type.</param>
    /// <returns>The failure, or <see langword="null"/>.</returns>
    public static TranscodeFailure? GetTranscodeFailure(this ITranscodeManager transcodeManager, string path, TranscodingJobType type)
        => (transcodeManager as IHardwareTranscodeFallback)?.GetTranscodeFailure(path, type);
}
