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
/// Compares the engine's decisions against the two phase-1-compatible seed fixtures from the
/// compatibility lab (PR93): direct play and remux. The dts-to-aac, downmix, and no-viable-plan
/// fixtures are intentionally not run here - they require transcoding (PR97) or assert reason codes
/// the phase-1 engine does not emit.
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

        Assert.Equal(expected.Output.Container, decision.Output.Container);
        Assert.Equal(expected.Output.VideoCodec, decision.Output.VideoCodec);
        Assert.Equal(expected.Output.AudioCodec, decision.Output.AudioCodec);

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

    private sealed record FixtureOutput(string? Container, string? VideoCodec, string? AudioCodec);
}
