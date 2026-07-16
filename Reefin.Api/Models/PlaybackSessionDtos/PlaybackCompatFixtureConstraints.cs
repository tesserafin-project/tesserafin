using System.Collections.Generic;
using Reefin.Playback.Decision;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// <see cref="PlaybackConstraints"/> minus <see cref="PlaybackConstraints.StartTimeTicks"/>, which
/// has no counterpart in the fixture schema's <c>constraints</c> object.
/// </summary>
/// <param name="AllowDirectPlay">See <see cref="PlaybackConstraints.AllowDirectPlay"/>.</param>
/// <param name="AllowDirectStream">See <see cref="PlaybackConstraints.AllowDirectStream"/>.</param>
/// <param name="AllowTranscoding">See <see cref="PlaybackConstraints.AllowTranscoding"/>.</param>
/// <param name="AllowVideoStreamCopy">See <see cref="PlaybackConstraints.AllowVideoStreamCopy"/>.</param>
/// <param name="AllowAudioStreamCopy">See <see cref="PlaybackConstraints.AllowAudioStreamCopy"/>.</param>
/// <param name="MaxBitrate">See <see cref="PlaybackConstraints.MaxBitrate"/>.</param>
/// <param name="MaxAudioChannels">See <see cref="PlaybackConstraints.MaxAudioChannels"/>.</param>
/// <param name="PreferredAudioStreamIndex">See <see cref="PlaybackConstraints.PreferredAudioStreamIndex"/>.</param>
/// <param name="PreferredSubtitleStreamIndex">See <see cref="PlaybackConstraints.PreferredSubtitleStreamIndex"/>.</param>
/// <param name="SubtitleMode">See <see cref="PlaybackConstraints.SubtitleMode"/>.</param>
/// <param name="PreferredSubtitleLanguages">See <see cref="PlaybackConstraints.PreferredSubtitleLanguages"/>.</param>
/// <param name="AlwaysBurnInSubtitleWhenTranscoding">See <see cref="PlaybackConstraints.AlwaysBurnInSubtitleWhenTranscoding"/>.</param>
public sealed record PlaybackCompatFixtureConstraints(
    bool AllowDirectPlay,
    bool AllowDirectStream,
    bool AllowTranscoding,
    bool AllowVideoStreamCopy,
    bool AllowAudioStreamCopy,
    int? MaxBitrate,
    int? MaxAudioChannels,
    int? PreferredAudioStreamIndex,
    int? PreferredSubtitleStreamIndex,
    SubtitlePlaybackMode SubtitleMode,
    IReadOnlyList<string> PreferredSubtitleLanguages,
    bool AlwaysBurnInSubtitleWhenTranscoding);
