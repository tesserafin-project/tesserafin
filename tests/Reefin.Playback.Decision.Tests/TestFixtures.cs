using System;
using System.Collections.Generic;
using Reefin.Playback.Decision;

namespace Reefin.Playback.Decision.Tests;

/// <summary>
/// Shared sample data used across the round-trip and invariant tests. Kept in one place so each
/// test only names the shape it cares about.
/// </summary>
internal static class TestFixtures
{
    public static ClientCapabilities SampleClientCapabilities() => new(
        Decode: new DecodeCapabilities(
            DirectPlayProfiles:
            [
                new DecodeProfile(MediaKind.Video, ["mp4", "mkv"], ["h264"], ["aac"]),
            ],
            VideoCodecs:
            [
                new VideoCodecCapability(
                    "h264",
                    ["high", "main"],
                    MaxLevel: 51,
                    MaxBitDepth: 8,
                    VideoRangeTypes: ["SDR"],
                    MaxResolution: new Resolution(1920, 1080),
                    MaxBitrate: 20_000_000),
            ],
            AudioCodecs:
            [
                new AudioCodecCapability("aac", MaxChannels: 6, MaxSampleRate: 48000, MaxBitDepth: 16, MaxBitrate: 384_000),
            ],
            SubtitleDelivery:
            [
                new SubtitleCapability("srt", SubtitleDeliveryMethod.External),
            ],
            SupportsHls: true,
            SupportsDash: false),
        OutputProfiles:
        [
            new PlaybackOutputProfile(
                Type: MediaKind.Video,
                Protocol: StreamingProtocol.Hls,
                Container: "ts",
                VideoCodecs: ["h264"],
                AudioCodecs: ["aac"],
                MaxVideoBitrate: 20_000_000,
                MaxAudioBitrate: 384_000,
                MaxAudioChannels: 2),
        ]);

    public static MediaSourceSnapshot SampleMediaSourceSnapshot() => new(
        MediaSourceId: "source-1",
        Container: "mkv",
        Protocol: "http",
        Bitrate: 15_000_000,
        RunTimeTicks: 72_000_000_000,
        VideoStreams:
        [
            new VideoStreamSnapshot(0, "h264", "high", 51, 1920, 1080, 8, "SDR", 23.976, 12_000_000, IsAnamorphic: false, IsInterlaced: false),
        ],
        AudioStreams:
        [
            new AudioStreamSnapshot(1, "dts", 6, 48000, 24, 1_500_000, "eng", IsDefault: true),
        ],
        SubtitleStreams:
        [
            new SubtitleStreamSnapshot(2, "srt", IsExternal: false, IsForced: false, IsDefault: false, Language: "eng"),
        ],
        SupportsDirectPlay: true,
        SupportsDirectStream: true,
        SupportsTranscoding: true);

    public static PlaybackConstraints SampleConstraints() => new(
        AllowDirectPlay: true,
        AllowDirectStream: true,
        AllowTranscoding: true,
        AllowVideoStreamCopy: true,
        AllowAudioStreamCopy: true,
        MaxBitrate: 20_000_000,
        MaxAudioChannels: 6,
        PreferredAudioStreamIndex: 1,
        PreferredSubtitleStreamIndex: null,
        SubtitleMode: SubtitlePlaybackMode.Default,
        PreferredSubtitleLanguages: ["eng"],
        AlwaysBurnInSubtitleWhenTranscoding: false,
        StartTimeTicks: 0);

    public static PlaybackRequestContext SampleRequestContext() => new(
        RequestId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ItemId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        MediaSourceId: "source-1",
        UserId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
        MediaKind: MediaKind.Video,
        RequestedAt: new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
        EngineVersion: 2);

    /// <summary>
    /// Builds the normative reasoning tree from RFC PR91 §5: direct play rejected because the
    /// container is unsupported, video is copyable, audio is not, so the decision remuxes with
    /// audio transcoded.
    /// </summary>
    public static ReasonNode SampleReasoningTree() => new(
        Code: ReasonCode.DirectPlayError,
        Outcome: ReasonOutcome.Rejected,
        Subject: ReasonSubject.Method(),
        Detail: null,
        Children:
        [
            new ReasonNode(
                Code: ReasonCode.ContainerNotSupported,
                Outcome: ReasonOutcome.Rejected,
                Subject: ReasonSubject.Container(),
                Detail: "got=mkv want=[mp4,ts]",
                Children:
                [
                    ReasonNode.Leaf(ReasonCode.StreamCopyable, ReasonOutcome.Accepted, ReasonSubject.VideoStream(0)),
                    new ReasonNode(
                        Code: ReasonCode.AudioCodecNotSupported,
                        Outcome: ReasonOutcome.Rejected,
                        Subject: ReasonSubject.AudioStream(1),
                        Detail: "got=dts want=[aac,ac3]",
                        Children:
                        [
                            ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method(), "Remux + TranscodeAudio(AAC)"),
                        ]),
                ]),
        ]);

    public static ReasonNode SampleNoViablePlanReasoning() => new(
        Code: ReasonCode.DirectPlayError,
        Outcome: ReasonOutcome.Rejected,
        Subject: ReasonSubject.Method(),
        Detail: null,
        Children:
        [
            ReasonNode.Leaf(ReasonCode.ContainerNotSupported, ReasonOutcome.Rejected, ReasonSubject.Container()),
            ReasonNode.Leaf(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method()),
        ]);

    public static SelectedStreams SampleSelectedStreams() => new(
        Video: 0,
        Audio: 1,
        Subtitle: new SelectedSubtitle(2, SubtitleDeliveryMethod.External));

    public static OutputSpec SampleOutputSpec() => new(
        Container: "mp4",
        VideoCodec: "h264",
        AudioCodec: "aac",
        Resolution: new Resolution(1920, 1080),
        VideoRange: "SDR",
        AudioChannels: 2,
        TotalBitrate: 8_000_000,
        VideoBitrate: 6_000_000,
        AudioBitrate: 2_000_000,
        Protocol: StreamingProtocol.Hls,
        SubtitleFormat: null);
}
