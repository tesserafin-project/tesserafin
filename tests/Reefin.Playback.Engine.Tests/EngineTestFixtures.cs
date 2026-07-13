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
        bool allowVideoStreamCopy = true,
        bool allowAudioStreamCopy = true,
        int? preferredAudioStreamIndex = null) => new(
        AllowDirectPlay: allowDirectPlay,
        AllowDirectStream: allowDirectStream,
        AllowTranscoding: true,
        AllowVideoStreamCopy: allowVideoStreamCopy,
        AllowAudioStreamCopy: allowAudioStreamCopy,
        MaxBitrate: null,
        MaxAudioChannels: null,
        PreferredAudioStreamIndex: preferredAudioStreamIndex,
        PreferredSubtitleStreamIndex: null,
        AlwaysBurnInSubtitleWhenTranscoding: false,
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

    public static VideoStreamSnapshot VideoStream(int index, string codec) => new(
        Index: index,
        Codec: codec,
        Profile: null,
        Level: null,
        Width: null,
        Height: null,
        BitDepth: null,
        VideoRange: null,
        Framerate: null,
        Bitrate: null,
        IsAnamorphic: false,
        IsInterlaced: false);

    public static AudioStreamSnapshot AudioStream(int index, string codec, bool isDefault = false) => new(
        Index: index,
        Codec: codec,
        Channels: null,
        SampleRate: null,
        BitDepth: null,
        Bitrate: null,
        Language: null,
        IsDefault: isDefault);

    public static MediaSourceSnapshot Source(
        string mediaSourceId,
        string container,
        IReadOnlyList<VideoStreamSnapshot>? videoStreams = null,
        IReadOnlyList<AudioStreamSnapshot>? audioStreams = null,
        bool supportsDirectPlay = true,
        bool supportsDirectStream = true) => new(
        MediaSourceId: mediaSourceId,
        Container: container,
        Protocol: "http",
        Bitrate: null,
        RunTimeTicks: null,
        VideoStreams: videoStreams ?? [],
        AudioStreams: audioStreams ?? [],
        SubtitleStreams: [],
        SupportsDirectPlay: supportsDirectPlay,
        SupportsDirectStream: supportsDirectStream,
        SupportsTranscoding: true);
}
