using Tesserafin.Model.Configuration;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// What a transcode manager knows about hardware that failed in this server process, and about
/// jobs that ended with a failure (tesserafin#119).
/// </summary>
/// <remarks>
/// A separate interface, not new members on <see cref="ITranscodeManager"/>: adding members to a
/// published interface breaks every implementation of it. The manager that ships implements
/// both; <see cref="HardwareTranscodeFallbackExtensions"/> reaches this one from the other.
/// </remarks>
public interface IHardwareTranscodeFallback
{
    /// <summary>
    /// Gets the encoding options a new command must be built with for this media source: the
    /// configured ones, with hardware acceleration withheld when a failure in this server
    /// process showed that the backend, or this source on it, cannot work.
    /// </summary>
    /// <remarks>
    /// Never the stored configuration object when it differs, and never saved: the
    /// administrator's choice is untouched and takes effect again at the next start.
    /// </remarks>
    /// <param name="mediaSourceId">The media source the command is for, when known.</param>
    /// <returns>The options to build with.</returns>
    EncodingOptions GetEffectiveEncodingOptions(string? mediaSourceId);

    /// <summary>
    /// Gets what is known about the failure of the job writing to <paramref name="path"/>, when
    /// that job ended on its own with one and has not been replaced or removed since.
    /// </summary>
    /// <param name="path">The job's output path.</param>
    /// <param name="type">The job type.</param>
    /// <returns>The failure, or <see langword="null"/>.</returns>
    TranscodeFailure? GetTranscodeFailure(string path, TranscodingJobType type);
}
