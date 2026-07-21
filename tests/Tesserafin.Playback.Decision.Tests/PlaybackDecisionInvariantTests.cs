using System;
using System.Text.Json;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Playback.Decision.Tests;

/// <summary>
/// Tests for the by-construction invariants enforced by <see cref="PlaybackDecision"/>'s static
/// factories. There is no public constructor, so every invalid state has to be reached through one
/// of these factories or not at all.
/// </summary>
public static class PlaybackDecisionInvariantTests
{
    [Fact]
    public static void Deserialize_InvalidState_Throws()
    {
        // The private constructor is also the [JsonConstructor] target for PlaybackDecisionJson
        // round-tripping, so it is a second entry point into this type besides the public
        // factories above. Validation lives in the constructor itself (not just the factories) so
        // this path cannot be used to reconstruct an invalid decision from crafted/corrupted JSON:
        // here, a "Transcode" decision with an empty Transforms list, which none of the public
        // factories could ever produce.
        const string invalidJson = """
            {
              "Method": "Transcode",
              "IsViable": true,
              "SelectedSource": "source-1",
              "SelectedStreams": { "Video": null, "Audio": null, "Subtitle": null },
              "Output": {},
              "Transforms": [],
              "Reasoning": { "Code": "MethodChosen", "Outcome": "Chosen", "Subject": { "Kind": "Method", "StreamIndex": null, "SourceId": null }, "Detail": null, "Children": [] },
              "EngineVersion": 1
            }
            """;

        Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<PlaybackDecision>(invalidJson, PlaybackDecisionJson.Options));
    }

    [Fact]
    public static void DirectPlay_AlwaysHasEmptyTransforms()
    {
        // DirectPlay's factory does not accept a transforms argument at all (see PR94 spec: the
        // signature is source/streams/output/reasoning/engineVersion only) - so "non-empty
        // transforms" cannot be constructed through the public API in the first place. The
        // invariant is enforced by omission rather than by a runtime check, so this test asserts
        // the guarantee positively instead of asserting a throw.
        var decision = PlaybackDecision.DirectPlay(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            TestFixtures.SampleReasoningTree(),
            engineVersion: 1);

        Assert.Empty(decision.Transforms);
    }

    [Fact]
    public static void DirectPlay_EmptySource_Throws()
    {
        Assert.Throws<ArgumentException>(() => PlaybackDecision.DirectPlay(
            string.Empty,
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            TestFixtures.SampleReasoningTree(),
            engineVersion: 1));
    }

    [Fact]
    public static void DirectPlay_EngineVersionZero_Throws()
    {
        Assert.Throws<ArgumentException>(() => PlaybackDecision.DirectPlay(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            TestFixtures.SampleReasoningTree(),
            engineVersion: 0));
    }

    [Fact]
    public static void Remux_WithTranscodeAudio_Throws()
    {
        Assert.Throws<ArgumentException>(() => PlaybackDecision.Remux(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.TranscodeAudio],
            TestFixtures.SampleReasoningTree(),
            engineVersion: 1));
    }

    [Fact]
    public static void Remux_WithoutRemuxContainer_Throws()
    {
        Assert.Throws<ArgumentException>(() => PlaybackDecision.Remux(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            [TransformKind.CopyVideo, TransformKind.CopyAudio],
            TestFixtures.SampleReasoningTree(),
            engineVersion: 1));
    }

    [Fact]
    public static void Remux_ValidTransforms_Succeeds()
    {
        var decision = PlaybackDecision.Remux(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.CopyAudio],
            TestFixtures.SampleReasoningTree(),
            engineVersion: 1);

        Assert.Equal(PlaybackMethod.Remux, decision.Method);
        Assert.True(decision.IsViable);
    }

    [Fact]
    public static void Transcode_EmptyTransforms_Throws()
    {
        Assert.Throws<ArgumentException>(() => PlaybackDecision.Transcode(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            [],
            TestFixtures.SampleReasoningTree(),
            engineVersion: 1));
    }

    [Fact]
    public static void Transcode_OnlyCopyTransforms_Throws()
    {
        Assert.Throws<ArgumentException>(() => PlaybackDecision.Transcode(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            [TransformKind.CopyVideo, TransformKind.CopyAudio],
            TestFixtures.SampleReasoningTree(),
            engineVersion: 1));
    }

    [Fact]
    public static void Transcode_WithTranscodeAudio_Succeeds()
    {
        var decision = PlaybackDecision.Transcode(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.TranscodeAudio],
            TestFixtures.SampleReasoningTree(),
            engineVersion: 1);

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.True(decision.IsViable);
    }

    [Fact]
    public static void NotViable_WithoutNoViablePlanNode_Throws()
    {
        var reasoningMissingCode = ReasonNode.Leaf(
            ReasonCode.ContainerNotSupported,
            ReasonOutcome.Rejected,
            ReasonSubject.Container());

        Assert.Throws<ArgumentException>(() => PlaybackDecision.NotViable(
            PlaybackMethod.DirectPlay,
            reasoningMissingCode,
            engineVersion: 1));
    }

    [Fact]
    public static void NotViable_EngineVersionZero_Throws()
    {
        Assert.Throws<ArgumentException>(() => PlaybackDecision.NotViable(
            PlaybackMethod.DirectPlay,
            TestFixtures.SampleNoViablePlanReasoning(),
            engineVersion: 0));
    }

    [Fact]
    public static void NotViable_Valid_HasExpectedShape()
    {
        var decision = PlaybackDecision.NotViable(
            PlaybackMethod.Transcode,
            TestFixtures.SampleNoViablePlanReasoning(),
            engineVersion: 1);

        Assert.False(decision.IsViable);
        Assert.Equal(string.Empty, decision.SelectedSource);
        Assert.Equal(SelectedStreams.None, decision.SelectedStreams);
        Assert.Equal(OutputSpec.Empty, decision.Output);
        Assert.Empty(decision.Transforms);
    }
}
