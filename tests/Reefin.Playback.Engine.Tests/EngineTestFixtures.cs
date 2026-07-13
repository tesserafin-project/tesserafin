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
        int? maxAudioChannels = null,
        int? preferredAudioStreamIndex = null,
        int? preferredSubtitleStreamIndex = null,
        bool alwaysBurnInSubtitleWhenTranscoding = false) => new(
        AllowDirectPlay: allowDirectPlay,
        AllowDirectStream: allowDirectStream,
        AllowTranscoding: allowTranscoding,
        AllowVideoStreamCopy: allowVideoStreamCopy,
        AllowAudioStreamCopy: allowAudioStreamCopy,
        MaxBitrate: null,
        MaxAudioChannels: maxAudioChannels,
        PreferredAudioStreamIndex: preferredAudioStreamIndex,
        PreferredSubtitleStreamIndex: preferredSubtitleStreamIndex,
        AlwaysBurnInSubtitleWhenTranscoding: alwaysBurnInSubtitleWhenTranscoding,
        StartTimeTicks: 0);

    public static ClientCapabilities Capabilities(IReadOnlyList<string> containers, IReadOnlyList<string> videoCodecs, IReadOnlyList<string> audioCodecs) => new(
        Containers: containers,
        VideoCodecs: videoCodecs.Select(codec => new VideoCodecCapability(codec, [], null, null, [])).ToList(),
        AudioCodecs: audioCodecs.Select(codec => new AudioCodecCapability(codec, null, null, null)).ToList(),
        SubtitleDelivery: [],
        MaxResolution: null,
        MaxVideoBitrate: null,
        MaxAudioBitrate: null,
        SupportsHls: false,
        SupportsDash: false);

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
        int? sampleRate = null) => new(
        Index: index,
        Codec: codec,
        Channels: channels,
        SampleRate: sampleRate,
        BitDepth: null,
        Bitrate: null,
        Language: null,
        IsDefault: isDefault);

    public static SubtitleStreamSnapshot SubtitleStream(
        int index,
        string format,
        bool isExternal = false,
        bool isForced = false,
        bool isDefault = false) => new(
        Index: index,
        Format: format,
        IsExternal: isExternal,
        IsForced: isForced,
        IsDefault: isDefault,
        Language: null);

    public static MediaSourceSnapshot Source(
        string mediaSourceId,
        string container,
        IReadOnlyList<VideoStreamSnapshot>? videoStreams = null,
        IReadOnlyList<AudioStreamSnapshot>? audioStreams = null,
        IReadOnlyList<SubtitleStreamSnapshot>? subtitleStreams = null,
        bool supportsDirectPlay = true,
        bool supportsDirectStream = true,
        bool supportsTranscoding = true) => new(
        MediaSourceId: mediaSourceId,
        Container: container,
        Protocol: "http",
        Bitrate: null,
        RunTimeTicks: null,
        VideoStreams: videoStreams ?? [],
        AudioStreams: audioStreams ?? [],
        SubtitleStreams: subtitleStreams ?? [],
        SupportsDirectPlay: supportsDirectPlay,
        SupportsDirectStream: supportsDirectStream,
        SupportsTranscoding: supportsTranscoding);
}
