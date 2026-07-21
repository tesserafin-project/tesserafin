using System.Collections.Generic;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Playback.Engine;

/// <summary>
/// Produces a <see cref="PlaybackDecision"/> for a playback request, given the client's
/// capabilities, the candidate media sources, and the constraints attached to the request.
/// </summary>
public interface IPlaybackEngine
{
    /// <summary>
    /// Decides how (and whether) to deliver playback for the given request.
    /// </summary>
    /// <param name="context">The who/what/when of the playback request.</param>
    /// <param name="capabilities">What the requesting client can read.</param>
    /// <param name="sources">The candidate media sources, in preference order.</param>
    /// <param name="constraints">The overrides and prohibitions attached to the request.</param>
    /// <returns>The resulting <see cref="PlaybackDecision"/>.</returns>
    PlaybackDecision Decide(
        PlaybackRequestContext context,
        ClientCapabilities capabilities,
        IReadOnlyList<MediaSourceSnapshot> sources,
        PlaybackConstraints constraints);
}
