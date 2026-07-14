using System.Collections.Generic;

namespace Reefin.Playback.Decision;

/// <summary>
/// The overrides and prohibitions attached to a playback request: what the caller allows or
/// prefers, independent of what the client or source are technically capable of.
/// </summary>
/// <param name="AllowDirectPlay">Whether direct play is permitted for this request.</param>
/// <param name="AllowDirectStream">Whether direct stream (remux) is permitted for this request.</param>
/// <param name="AllowTranscoding">Whether transcoding is permitted for this request.</param>
/// <param name="AllowVideoStreamCopy">Whether the video stream may be copied without re-encoding.</param>
/// <param name="AllowAudioStreamCopy">Whether the audio stream may be copied without re-encoding.</param>
/// <param name="MaxBitrate">The maximum overall bitrate allowed, or <see langword="null"/> if unbounded.</param>
/// <param name="MaxAudioChannels">The maximum audio channel count allowed, or <see langword="null"/> if unbounded.</param>
/// <param name="PreferredAudioStreamIndex">The preferred audio stream index, or <see langword="null"/> for no preference.</param>
/// <param name="PreferredSubtitleStreamIndex">The preferred subtitle stream index, or <see langword="null"/> for no preference.</param>
/// <param name="SubtitleMode">
/// How to auto-select a subtitle stream (PR103) when <paramref name="PreferredSubtitleStreamIndex"/>
/// is <see langword="null"/> - mirrors the legacy user-level <c>SubtitlePlaybackMode</c> preference
/// consumed by <c>MediaStreamSelector.GetDefaultSubtitleStreamIndex</c>.
/// </param>
/// <param name="PreferredSubtitleLanguages">
/// The preferred subtitle languages, in preference order (PR103). An empty list is a wildcard -
/// matches any language - the same semantics as the legacy
/// <c>MediaStreamSelector.MatchesPreferredLanguage</c>.
/// </param>
/// <param name="AlwaysBurnInSubtitleWhenTranscoding">Whether subtitles must always be burned in whenever transcoding occurs, regardless of client subtitle capability.</param>
/// <param name="StartTimeTicks">The playback start offset, in ticks.</param>
public sealed record PlaybackConstraints(
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
    bool AlwaysBurnInSubtitleWhenTranscoding,
    long StartTimeTicks);
