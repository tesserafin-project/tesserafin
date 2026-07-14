using System;
using System.Text.Json;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Playback.Decision.Tests;

/// <summary>
/// Round-trip serialization tests for every top-level domain type. Record equality does not deep
/// compare <see cref="System.Collections.Generic.IReadOnlyList{T}"/> members (reference equality
/// on the underlying collection instance instead), so these tests do not call <c>Assert.Equal</c>
/// on the deserialized object. Instead: serialize, deserialize, re-serialize, and compare the two
/// JSON strings - a value-correct round trip produces identical JSON both times.
/// </summary>
public static class SerializationRoundTripTests
{
    [Fact]
    public static void PlaybackDecision_DirectPlay_RoundTrips()
    {
        var decision = PlaybackDecision.DirectPlay(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            TestFixtures.SampleReasoningTree(),
            engineVersion: 2);

        AssertRoundTrips(decision);
    }

    [Fact]
    public static void PlaybackDecision_Remux_RoundTrips()
    {
        var decision = PlaybackDecision.Remux(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.CopyAudio],
            TestFixtures.SampleReasoningTree(),
            engineVersion: 2);

        AssertRoundTrips(decision);
    }

    [Fact]
    public static void PlaybackDecision_Transcode_RoundTrips()
    {
        var decision = PlaybackDecision.Transcode(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.TranscodeAudio],
            TestFixtures.SampleReasoningTree(),
            engineVersion: 2);

        AssertRoundTrips(decision);
    }

    [Fact]
    public static void PlaybackDecision_NotViable_RoundTrips()
    {
        var decision = PlaybackDecision.NotViable(
            PlaybackMethod.DirectPlay,
            TestFixtures.SampleNoViablePlanReasoning(),
            engineVersion: 2);

        AssertRoundTrips(decision);
    }

    [Fact]
    public static void ClientCapabilities_RoundTrips()
    {
        AssertRoundTrips(TestFixtures.SampleClientCapabilities());
    }

    [Fact]
    public static void MediaSourceSnapshot_RoundTrips()
    {
        AssertRoundTrips(TestFixtures.SampleMediaSourceSnapshot());
    }

    [Fact]
    public static void PlaybackRequestContext_RoundTrips()
    {
        AssertRoundTrips(TestFixtures.SampleRequestContext());
    }

    [Fact]
    public static void PlaybackConstraints_RoundTrips()
    {
        AssertRoundTrips(TestFixtures.SampleConstraints());
    }

    [Fact]
    public static void ReasonNodeTree_RoundTrips()
    {
        AssertRoundTrips(TestFixtures.SampleReasoningTree());
    }

    [Fact]
    public static void OutputSpec_Protocol_IsSerialized()
    {
        // RFC PR102b: OutputSpec.Protocol must actually appear in the wire format - a client
        // reading the JSON cannot infer HLS-vs-HTTP delivery from any other field.
        var decision = PlaybackDecision.Transcode(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            [TransformKind.RemuxContainer, TransformKind.CopyVideo, TransformKind.TranscodeAudio],
            TestFixtures.SampleReasoningTree(),
            engineVersion: 2);

        var json = JsonSerializer.Serialize(decision, PlaybackDecisionJson.Options);

        Assert.Contains("\"Protocol\":\"Hls\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public static void Enums_SerializeAsStrings_NotNumbers()
    {
        var decision = PlaybackDecision.DirectPlay(
            "source-1",
            TestFixtures.SampleSelectedStreams(),
            TestFixtures.SampleOutputSpec(),
            TestFixtures.SampleReasoningTree(),
            engineVersion: 2);

        var json = JsonSerializer.Serialize(decision, PlaybackDecisionJson.Options);

        Assert.Contains("\"Method\":\"DirectPlay\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Delivery\":\"External\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Code\":\"DirectPlayError\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Outcome\":\"Rejected\"", json, StringComparison.Ordinal);

        var context = TestFixtures.SampleRequestContext();
        var contextJson = JsonSerializer.Serialize(context, PlaybackDecisionJson.Options);
        Assert.Contains("\"MediaKind\":\"Video\"", contextJson, StringComparison.Ordinal);
    }

    private static void AssertRoundTrips<T>(T value)
    {
        var json1 = JsonSerializer.Serialize(value, PlaybackDecisionJson.Options);
        var deserialized = JsonSerializer.Deserialize<T>(json1, PlaybackDecisionJson.Options);
        var json2 = JsonSerializer.Serialize(deserialized, PlaybackDecisionJson.Options);

        Assert.Equal(json1, json2);
    }
}
