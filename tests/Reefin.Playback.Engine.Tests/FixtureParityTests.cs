using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reefin.Playback.Decision;
using Xunit;

namespace Reefin.Playback.Engine.Tests;

/// <summary>
/// Compares the engine's decisions against all ten fixtures from the compatibility lab (PR93):
/// direct play, remux, audio-transcode, downmix, no-viable-plan, video-codec-incompatible,
/// bitrate/resolution limit, HDR tonemap, subtitle burn-in, and subtitle external delivery. Phase 2
/// (PR97) implements transcoding, subtitle handling, resolution/bitrate limits, codec profile/
/// level/bit-depth checks, HDR tonemapping, and channel downmix, so all ten now run here.
/// </summary>
public static class FixtureParityTests
{
    // Test-local, deliberately NOT PlaybackDecisionJson.Options: the fixtures use camelCase
    // property names, the domain records are PascalCase, and PlaybackDecisionJson.Options is
    // case-sensitive. Reusing it would silently bind every field to null instead of failing loudly.
    private static readonly JsonSerializerOptions FixtureOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData("video-h264-aac-mp4-directplay.json")]
    [InlineData("video-mkv-remux-mp4.json")]
    [InlineData("video-mkv-dts-to-aac.json")]
    [InlineData("audio-downmix-51-to-stereo.json")]
    [InlineData("video-no-viable-plan.json")]
    [InlineData("video-codec-incompatible.json")]
    [InlineData("video-resolution-limit.json")]
    [InlineData("video-hdr-tonemap.json")]
    [InlineData("subtitle-pgs-burn-in.json")]
    [InlineData("subtitle-srt-external.json")]
    public static void Fixture_EngineDecisionMatchesExpected(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName);
        var json = File.ReadAllText(path);
        var fixture = JsonSerializer.Deserialize<FixtureFile>(json, FixtureOptions)
            ?? throw new InvalidOperationException($"Fixture '{fixtureName}' deserialized to null.");

        var engine = new PlaybackEngine();
        IReadOnlyList<MediaSourceSnapshot> sources = [fixture.Input.Source];
        var decision = engine.Decide(fixture.Input.Context, fixture.Input.Capabilities, sources, fixture.Input.Constraints);

        var expected = fixture.Expected;

        Assert.Equal(Enum.Parse<PlaybackMethod>(expected.Method), decision.Method);
        Assert.Equal(expected.IsViable, decision.IsViable);

        Assert.Equal(expected.SelectedStreams.Video, decision.SelectedStreams.Video);
        Assert.Equal(expected.SelectedStreams.Audio, decision.SelectedStreams.Audio);
        Assert.Equal(expected.SelectedStreams.Subtitle, decision.SelectedStreams.Subtitle?.Index);

        // The fixture's `expected.output` only declares the fields relevant to the case it's
        // isolating; an absent field deserializes to null on FixtureOutput, which is exactly what
        // the engine is expected to produce when that field is not applicable/unchanged.
        Assert.Equal(expected.Output.Container, decision.Output.Container);
        Assert.Equal(expected.Output.VideoCodec, decision.Output.VideoCodec);
        Assert.Equal(expected.Output.AudioCodec, decision.Output.AudioCodec);
        Assert.Equal(expected.Output.Resolution, decision.Output.Resolution);
        Assert.Equal(expected.Output.VideoRange, decision.Output.VideoRange);
        Assert.Equal(expected.Output.AudioChannels, decision.Output.AudioChannels);

        var expectedTransforms = expected.Transforms.Select(Enum.Parse<TransformKind>).ToHashSet();
        var actualTransforms = decision.Transforms.ToHashSet();
        Assert.Equal(expectedTransforms, actualTransforms);

        var expectedReasonCodes = expected.ReasonCodes.Select(Enum.Parse<ReasonCode>).ToHashSet();
        var actualReasonCodes = FlattenReasonCodes(decision.Reasoning).ToHashSet();
        Assert.Equal(expectedReasonCodes, actualReasonCodes);
    }

    private static IEnumerable<ReasonCode> FlattenReasonCodes(ReasonNode node)
    {
        yield return node.Code;

        foreach (var child in node.Children)
        {
            foreach (var code in FlattenReasonCodes(child))
            {
                yield return code;
            }
        }
    }

    private sealed record FixtureFile(FixtureInput Input, FixtureExpected Expected);

    private sealed record FixtureInput(
        PlaybackRequestContext Context,
        ClientCapabilities Capabilities,
        MediaSourceSnapshot Source,
        PlaybackConstraints Constraints);

    private sealed record FixtureExpected(
        string Method,
        FixtureSelectedStreams SelectedStreams,
        FixtureOutput Output,
        IReadOnlyList<string> Transforms,
        IReadOnlyList<string> ReasonCodes,
        bool IsViable);

    private sealed record FixtureSelectedStreams(int? Video, int? Audio, int? Subtitle);

    private sealed record FixtureOutput(
        string? Container,
        string? VideoCodec,
        string? AudioCodec,
        Resolution? Resolution,
        string? VideoRange,
        int? AudioChannels);
}
