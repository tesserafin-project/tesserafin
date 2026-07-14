using System;
using System.Collections.Generic;
using System.Linq;
using Reefin.Playback.Decision;

namespace Reefin.Playback.Engine.Tests;

/// <summary>
/// Small builders for the domain records the engine consumes, so each test only names the field it
/// cares about instead of repeating every positional constructor argument.
/// </summary>
internal static class EngineTestFixtures
{
    public static PlaybackRequestContext Context(MediaKind mediaKind) => new(
        RequestId: Guid.NewGuid(),
        ItemId: Guid.NewGuid(),
        MediaSourceId: null,
        UserId: Guid.NewGuid(),
        MediaKind: mediaKind,
        RequestedAt: DateTimeOffset.UtcNow,
        EngineVersion: PlaybackEngine.EngineVersion);

    public static PlaybackConstraints Constraints(
        bool allowDirectPlay = true,
        bool allowDirectStream = true,
        bool allowTranscoding = true,
        bool allowVideoStreamCopy = true,
        bool allowAudioStreamCopy = true,
        int? maxBitrate = null,
        int? maxAudioChannels = null,
        int? preferredAudioStreamIndex = null,
        int? preferredSubtitleStreamIndex = null,
        SubtitlePlaybackMode subtitleMode = SubtitlePlaybackMode.Default,
        IReadOnlyList<string>? preferredSubtitleLanguages = null,
        bool alwaysBurnInSubtitleWhenTranscoding = false) => new(
        AllowDirectPlay: allowDirectPlay,
        AllowDirectStream: allowDirectStream,
        AllowTranscoding: allowTranscoding,
        AllowVideoStreamCopy: allowVideoStreamCopy,
        AllowAudioStreamCopy: allowAudioStreamCopy,
        MaxBitrate: maxBitrate,
        MaxAudioChannels: maxAudioChannels,
        PreferredAudioStreamIndex: preferredAudioStreamIndex,
        PreferredSubtitleStreamIndex: preferredSubtitleStreamIndex,
        SubtitleMode: subtitleMode,
        PreferredSubtitleLanguages: preferredSubtitleLanguages ?? [],
        AlwaysBurnInSubtitleWhenTranscoding: alwaysBurnInSubtitleWhenTranscoding,
        StartTimeTicks: 0);

    public static ClientCapabilities Capabilities(
        IReadOnlyList<string> containers,
        IReadOnlyList<string> videoCodecs,
        IReadOnlyList<string> audioCodecs,
        IReadOnlyList<PlaybackOutputProfile>? outputProfiles = null) => new(
        // Mirrors a real device profile's shape: a Video-type DecodeProfile carrying container +
        // video codec(s) + audio codec(s) together, and a separate Audio-type DecodeProfile
        // carrying container + audio codec(s) only - both declared unconditionally here so this
        // shared builder still direct-plays for both MediaKind.Video and MediaKind.Audio tests
        // (PR102b: DecodeProfile is MediaKind-keyed, so a caller needing only one axis still gets a
        // matching profile for it).
        Decode: new DecodeCapabilities(
            DirectPlayProfiles:
            [
                new DecodeProfile(MediaKind.Video, containers, videoCodecs, audioCodecs),
                new DecodeProfile(MediaKind.Audio, containers, [], audioCodecs),
            ],
            VideoCodecs: videoCodecs.Select(codec => new VideoCodecCapability(codec, [], null, null, [], null, null)).ToList(),
            AudioCodecs: audioCodecs.Select(codec => new AudioCodecCapability(codec, null, null, null, null)).ToList(),
            SubtitleDelivery: [],
            SupportsHls: false,
            SupportsDash: false),
        OutputProfiles: outputProfiles ?? []);

    public static VideoStreamSnapshot VideoStream(
        int index,
        string codec,
        string? profile = null,
        double? level = null,
        int? width = null,
        int? height = null,
        int? bitDepth = null,
        string? videoRange = null,
        int? bitrate = null) => new(
        Index: index,
        Codec: codec,
        Profile: profile,
        Level: level,
        Width: width,
        Height: height,
        BitDepth: bitDepth,
        VideoRange: videoRange,
        Framerate: null,
        Bitrate: bitrate,
        IsAnamorphic: false,
        IsInterlaced: false);

    public static AudioStreamSnapshot AudioStream(
        int index,
        string codec,
        bool isDefault = false,
        int? channels = null,
        int? sampleRate = null,
        int? bitrate = null,
        string? language = null) => new(
        Index: index,
        Codec: codec,
        Channels: channels,
        SampleRate: sampleRate,
        BitDepth: null,
        Bitrate: bitrate,
        Language: language,
        IsDefault: isDefault);

    public static SubtitleStreamSnapshot SubtitleStream(
        int index,
        string format,
        bool isExternal = false,
        bool isForced = false,
        bool isDefault = false,
        string? language = null) => new(
        Index: index,
        Format: format,
        IsExternal: isExternal,
        IsForced: isForced,
        IsDefault: isDefault,
        Language: language);

    public static MediaSourceSnapshot Source(
        string mediaSourceId,
        string container,
        IReadOnlyList<VideoStreamSnapshot>? videoStreams = null,
        IReadOnlyList<AudioStreamSnapshot>? audioStreams = null,
        IReadOnlyList<SubtitleStreamSnapshot>? subtitleStreams = null,
        int? bitrate = null,
        bool supportsDirectPlay = true,
        bool supportsDirectStream = true,
        bool supportsTranscoding = true) => new(
        MediaSourceId: mediaSourceId,
        Container: container,
        Protocol: "http",
        Bitrate: bitrate,
        RunTimeTicks: null,
        VideoStreams: videoStreams ?? [],
        AudioStreams: audioStreams ?? [],
        SubtitleStreams: subtitleStreams ?? [],
        SupportsDirectPlay: supportsDirectPlay,
        SupportsDirectStream: supportsDirectStream,
        SupportsTranscoding: supportsTranscoding);
}
