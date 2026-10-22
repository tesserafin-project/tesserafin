using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Whether a video is an active recording, which its source type reads, on top of
    /// <see cref="IStandaloneVisibilityService"/>.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="Video"/> (DEBT-SERVER-9): a video's source type asks the static
    /// <c>Video.RecordingsManager</c>, which depends on <see cref="ILibraryManager"/>. Passed as the
    /// <see cref="ITagVisibilityService"/> of its visibility check, it answers instead, while the rules stay the
    /// entities' own. Implementations must not depend on <see cref="ILibraryManager"/>, directly or deferred.
    /// </remarks>
    public interface IRecordingVisibilityService : IStandaloneVisibilityService
    {
        /// <summary>
        /// Gets a value indicating whether a recording is in progress at a path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns><c>true</c> if a recording is in progress at that path.</returns>
        bool IsActiveRecording(string path);
    }
}
