using System;
using Reefin.Model.Dlna;
using Reefin.Playback.Decision;

namespace Reefin.Playback.Dlna;

/// <summary>
/// Maps the legacy <see cref="MediaOptions"/> request options to the domain
/// <see cref="PlaybackConstraints"/>.
/// </summary>
public static class PlaybackConstraintsMapper
{
    /// <summary>
    /// Projects legacy <see cref="MediaOptions"/> into domain <see cref="PlaybackConstraints"/>.
    /// </summary>
    /// <param name="options">The legacy media options to project.</param>
    /// <returns>The equivalent domain constraints.</returns>
    /// <remarks>
    /// <see cref="PlaybackConstraints.AllowTranscoding"/> is always projected as <see langword="true"/>.
    /// <see cref="MediaOptions"/> has no per-request transcoding toggle: a per-source ban on
    /// transcoding is already carried by <see cref="MediaSourceSnapshot.SupportsTranscoding"/>, so it
    /// is not lost, only relocated. <see cref="PlaybackConstraints.StartTimeTicks"/> is always
    /// projected as <c>0</c> because <see cref="MediaOptions"/> carries no start offset.
    /// </remarks>
    /// <remarks>
    /// PR103 scope boundary: <see cref="PlaybackConstraints.SubtitleMode"/> and
    /// <see cref="PlaybackConstraints.PreferredSubtitleLanguages"/> always project to
    /// <see cref="SubtitlePlaybackMode.Default"/> and an empty list. <see cref="MediaOptions"/>
    /// carries no user-level subtitle mode/language preference field at all - those live on
    /// <c>Reefin.Model.Configuration.UserConfiguration</c> (<c>SubtitleMode</c>,
    /// <c>SubtitleLanguagePreference</c>), consumed only by
    /// <c>MediaSourceManager.SetDefaultSubtitleStreamIndex</c> upstream of this mapper, not
    /// forwarded onto <see cref="MediaOptions"/>. Callers that already resolved a default via that
    /// legacy path continue to pass it explicitly as <see cref="MediaOptions.SubtitleStreamIndex"/>
    /// (projected to <see cref="PlaybackConstraints.PreferredSubtitleStreamIndex"/> above), which
    /// takes priority over auto-selection regardless. The default mirrors
    /// <c>SubtitlePlaybackMode.Default</c>'s own numeric default (member value 0), the common case.
    /// </remarks>
    public static PlaybackConstraints ToConstraints(MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new PlaybackConstraints(
            AllowDirectPlay: options.EnableDirectPlay,
            AllowDirectStream: options.EnableDirectStream,
            AllowTranscoding: true,
            AllowVideoStreamCopy: options.AllowVideoStreamCopy,
            AllowAudioStreamCopy: options.AllowAudioStreamCopy,
            MaxBitrate: options.MaxBitrate,
            MaxAudioChannels: options.MaxAudioChannels,
            PreferredAudioStreamIndex: options.AudioStreamIndex,
            PreferredSubtitleStreamIndex: options.SubtitleStreamIndex,
            SubtitleMode: SubtitlePlaybackMode.Default,
            PreferredSubtitleLanguages: [],
            AlwaysBurnInSubtitleWhenTranscoding: options.AlwaysBurnInSubtitleWhenTranscoding,
            StartTimeTicks: 0);
    }
}
