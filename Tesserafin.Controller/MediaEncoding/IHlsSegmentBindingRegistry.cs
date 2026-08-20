namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Resolves the authoritative owner of a legacy HLS playlist's segment files (#153-LTV-R1).
/// </summary>
/// <remarks>
/// Deliberately a separate interface rather than three more members on
/// <see cref="ITranscodeManager"/>: the segment route needs exactly this and nothing else, and a
/// narrow interface is what lets the route be tested without standing up a transcode manager.
/// </remarks>
public interface IHlsSegmentBindingRegistry
{
    /// <summary>
    /// Returns the binding for a playlist identifier, or <see langword="null"/> if no active job
    /// owns it.
    /// </summary>
    /// <param name="playlistId">The playlist identifier the route named.</param>
    /// <returns>The binding, or <see langword="null"/>.</returns>
    HlsSegmentBinding? ResolveByPlaylistId(string playlistId);
}
