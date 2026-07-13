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
            AlwaysBurnInSubtitleWhenTranscoding: options.AlwaysBurnInSubtitleWhenTranscoding,
            StartTimeTicks: 0);
    }
}
