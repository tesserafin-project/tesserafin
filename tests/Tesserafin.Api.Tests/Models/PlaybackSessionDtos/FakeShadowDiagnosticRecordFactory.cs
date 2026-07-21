using System;
using System.Collections.Generic;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Shadow;

namespace Tesserafin.Api.Tests.Models.PlaybackSessionDtos;

/// <summary>
/// Builds a minimal, valid <see cref="ShadowDiagnosticRecord"/> for tests that need one retained -
/// the controller/mapper tests care about which fields are populated/null on the resulting
/// <see cref="Tesserafin.Api.Models.PlaybackSessionDtos.PlaybackDiagnosticDetail"/>, not the specific
/// values inside each nested type.
/// </summary>
internal static class FakeShadowDiagnosticRecordFactory
{
    public static ShadowDiagnosticRecord Create() => new(
        Decision: PlaybackDecision.DirectPlay(
            "source-1",
            SelectedStreams.None,
            OutputSpec.Empty,
            ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
            engineVersion: 1),
        LegacyVector: new DecisionVector(
            IsViable: true,
            Method: NormalizedMethod.DirectPlay,
            VideoStreamIndex: StreamSelection.Unknown,
            AudioStreamIndex: StreamSelection.Selected(0),
            SubtitleStreamIndex: StreamSelection.None,
            TransformClasses: new HashSet<TransformClass>(),
            ReasonCategories: new HashSet<ReasonCategory> { ReasonCategory.VideoCodec },
            OutputContainer: "mp4",
            OutputVideoCodec: "h264",
            OutputAudioCodec: "aac",
            SelectedSource: "source-1",
            OutputWidth: 1920,
            OutputHeight: 1080,
            OutputBitrate: 5_000_000,
            OutputVideoRange: "SDR",
            OutputAudioChannels: 2,
            SubtitleDeliveryMode: null,
            OutputSubtitleFormat: null),
        Divergence: new ShadowDivergence(
            Class: DivergenceClass.Equivalent,
            MethodDiffers: false,
            StreamsDiffer: false,
            OnlyLegacy: new HashSet<TransformClass>(),
            OnlyV2: new HashSet<TransformClass>(),
            ReasonOnlyLegacy: new HashSet<ReasonCategory>(),
            ReasonOnlyV2: new HashSet<ReasonCategory>(),
            Summary: "equivalent"),
        Context: new PlaybackRequestContext(
            RequestId: Guid.NewGuid(),
            ItemId: Guid.NewGuid(),
            MediaSourceId: "source-1",
            UserId: Guid.Empty,
            MediaKind: MediaKind.Video,
            RequestedAt: DateTimeOffset.UtcNow,
            EngineVersion: 1),
        Capabilities: new ClientCapabilities(
            Decode: new DecodeCapabilities([], [], [], [], SupportsHls: false, SupportsDash: false),
            OutputProfiles: []),
        Sources: [],
        Constraints: new PlaybackConstraints(
            AllowDirectPlay: true,
            AllowDirectStream: true,
            AllowTranscoding: true,
            AllowVideoStreamCopy: true,
            AllowAudioStreamCopy: true,
            MaxBitrate: null,
            MaxAudioChannels: null,
            PreferredAudioStreamIndex: null,
            PreferredSubtitleStreamIndex: null,
            SubtitleMode: SubtitlePlaybackMode.Default,
            PreferredSubtitleLanguages: [],
            AlwaysBurnInSubtitleWhenTranscoding: false,
            StartTimeTicks: 0),
        Kind: PlaybackMediaKind.Video,
        CapturedAt: DateTimeOffset.UtcNow);
}
