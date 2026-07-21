using System;
using Tesserafin.Model.Dlna;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Playback.Dlna;

/// <summary>
/// Applies domain <see cref="PlaybackConstraints"/> onto an existing legacy <see cref="MediaOptions"/>.
/// </summary>
/// <remarks>
/// TEMPORARY (PR112b) - see <see cref="ReverseClientCapabilitiesMapper"/>'s remarks for why this
/// exists and when to delete it (PR114a).
/// </remarks>
/// <remarks>
/// The structural inverse of <see cref="PlaybackConstraintsMapper"/>, but not a full constructor:
/// <see cref="MediaOptions"/> has a <see langword="required"/> <see cref="MediaOptions.Profile"/>
/// plus several server-resolved fields (<see cref="MediaOptions.ItemId"/>,
/// <see cref="MediaOptions.MediaSources"/>) that <see cref="PlaybackConstraints"/> knows nothing
/// about, so this mutates an already-otherwise-populated instance in place rather than constructing
/// a new one. <see cref="PlaybackConstraints.AllowTranscoding"/>,
/// <see cref="PlaybackConstraints.SubtitleMode"/>,
/// <see cref="PlaybackConstraints.PreferredSubtitleLanguages"/>, and
/// <see cref="PlaybackConstraints.StartTimeTicks"/> have no <see cref="MediaOptions"/> field to land
/// in (see <see cref="PlaybackConstraintsMapper"/>'s remarks for why the forward direction always
/// projects them to fixed defaults) - <see cref="PlaybackConstraints.AllowTranscoding"/> is legacy's
/// per-<c>MediaSourceInfo.SupportsTranscoding</c> concept instead, which callers apply themselves
/// (mirrors what <c>PlaybackSessionsController</c> already did with the old
/// <c>EnableTranscoding</c> request field), and the other three are simply not carried forward.
/// </remarks>
public static class ReverseConstraintsMapper
{
    /// <summary>
    /// Applies domain <see cref="PlaybackConstraints"/> onto an existing <see cref="MediaOptions"/>,
    /// overwriting every field <see cref="PlaybackConstraintsMapper.ToConstraints"/> reads.
    /// </summary>
    /// <param name="options">The media options to apply the constraints onto.</param>
    /// <param name="constraints">The domain constraints to apply.</param>
    public static void ApplyTo(MediaOptions options, PlaybackConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(constraints);

        options.EnableDirectPlay = constraints.AllowDirectPlay;
        options.EnableDirectStream = constraints.AllowDirectStream;
        options.AllowVideoStreamCopy = constraints.AllowVideoStreamCopy;
        options.AllowAudioStreamCopy = constraints.AllowAudioStreamCopy;
        options.MaxBitrate = constraints.MaxBitrate;
        options.MaxAudioChannels = constraints.MaxAudioChannels;
        options.AudioStreamIndex = constraints.PreferredAudioStreamIndex;
        options.SubtitleStreamIndex = constraints.PreferredSubtitleStreamIndex;
        options.AlwaysBurnInSubtitleWhenTranscoding = constraints.AlwaysBurnInSubtitleWhenTranscoding;
    }
}
