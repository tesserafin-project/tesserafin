using System;
using System.Collections.Generic;
using Reefin.Controller.MediaEncoding;
using Reefin.MediaEncoding.Playback;
using Reefin.Playback.Decision;
using Reefin.Playback.Shadow;
using Xunit;

namespace Reefin.MediaEncoding.Tests.Playback;

/// <summary>
/// Unit tests for the ambient capture-scope hardening added in PR113a: <see cref="IShadowDiagnosticsStore.Publish"/>
/// called with no <see cref="IShadowDiagnosticsStore.BeginCapture"/> scope open must not throw or
/// corrupt state, and nested <see cref="IShadowDiagnosticsStore.BeginCapture"/> scopes must restore
/// the enclosing scope's state - not simply clear it - when the inner scope's disposable is disposed.
/// </summary>
public class InMemoryShadowDiagnosticsStoreTests
{
    [Fact]
    public void Publish_NoActiveScope_IsIgnoredAndDoesNotThrow()
    {
        var store = new InMemoryShadowDiagnosticsStore();

        var exception = Record.Exception(() => store.Publish(CreateRecord()));

        Assert.Null(exception);
    }

    [Fact]
    public void TakeCaptured_NoActiveScope_ReturnsNull()
    {
        var store = new InMemoryShadowDiagnosticsStore();

        Assert.Null(store.TakeCaptured());
    }

    [Fact]
    public void Publish_AfterScopeDisposed_IsIgnoredAndDoesNotLeakIntoNextScope()
    {
        var store = new InMemoryShadowDiagnosticsStore();

        using (store.BeginCapture())
        {
            store.Publish(CreateRecord());
        }

        // The scope closed above is gone; this Publish has nothing open to write into.
        store.Publish(CreateRecord());

        using var nextScope = store.BeginCapture();
        Assert.Null(store.TakeCaptured());
    }

    [Fact]
    public void BeginCapture_NestedScope_RestoresParentStateOnInnerDispose()
    {
        var store = new InMemoryShadowDiagnosticsStore();
        var outerRecord = CreateRecord();
        var innerRecord = CreateRecord();

        using (store.BeginCapture())
        {
            store.Publish(outerRecord);
            Assert.Same(outerRecord, store.TakeCaptured());

            using (store.BeginCapture())
            {
                store.Publish(innerRecord);
                Assert.Same(innerRecord, store.TakeCaptured());
            }

            // Inner scope disposed: the outer scope's own captured record must reappear untouched,
            // not be wiped to null.
            Assert.Same(outerRecord, store.TakeCaptured());
        }
    }

    [Fact]
    public void BeginCapture_OuterScopeDispose_ClearsToNoActiveScope()
    {
        var store = new InMemoryShadowDiagnosticsStore();

        using (store.BeginCapture())
        {
            store.Publish(CreateRecord());
        }

        Assert.Null(store.TakeCaptured());
        var exception = Record.Exception(() => store.Publish(CreateRecord()));
        Assert.Null(exception);
    }

    [Fact]
    public void CaptureScope_Dispose_IsIdempotent()
    {
        var store = new InMemoryShadowDiagnosticsStore();
        var scope = store.BeginCapture();
        store.Publish(CreateRecord());

        scope.Dispose();
        var exception = Record.Exception(() => scope.Dispose());

        Assert.Null(exception);
        Assert.Null(store.TakeCaptured());
    }

    /// <summary>
    /// Builds a minimal, valid <see cref="ShadowDiagnosticRecord"/> - these tests only care about
    /// scope plumbing (which instance is captured when), not the values inside the record.
    /// </summary>
    private static ShadowDiagnosticRecord CreateRecord() => new(
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
