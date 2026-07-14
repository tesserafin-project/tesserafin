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
/// Compares the engine's decisions against every fixture in the compatibility lab (PR93, format v5
/// as of PR104): direct play, remux, audio-transcode, downmix, no-viable-plan,
/// video-codec-incompatible, bitrate/resolution limit, HDR tonemap, subtitle burn-in, subtitle
/// external delivery, live TV, alternate versions, requested-source selection, direct-play
/// container/codec cross-mismatch, per-codec limits, output-profile ordering, HTTP/HLS protocol
/// selection, subtitle auto-selection, invalid stream indices, codec-level bitrate limits, and AV1
/// preference.
/// </summary>
/// <remarks>
/// PR104: fixtures now carry <c>sources</c> (a list, replacing the single v1-v4 <c>source</c>) and
/// an optional top-level <c>requestedMediaSourceId</c>; deserialization is strict
/// (<see cref="JsonUnmappedMemberHandling.Disallow"/>) so an unknown fixture property fails the
/// test loudly instead of silently binding to nothing; <c>fixtureVersion</c>/<c>id</c>/
/// <c>category</c>/<c>engineVersion</c> are now asserted, not just parsed. Structural validation
/// (required properties, enum membership, additionalProperties:false) is covered separately by
/// <see cref="FixtureSchemaValidationTests"/> against tests/PlaybackCompat/schema/fixture.schema.json;
/// this class is the behavioral gate (does the engine actually produce what the fixture expects).
/// </remarks>
public static class FixtureParityTests
{
    // Test-local, deliberately NOT PlaybackDecisionJson.Options: the fixtures use camelCase
    // property names, the domain records are PascalCase, and PlaybackDecisionJson.Options is
    // case-sensitive. Reusing it would silently bind every field to null instead of failing loudly.
    // UnmappedMemberHandling.Disallow (PR104) turns a stray/misspelled/stale fixture property into a
    // hard test failure instead of a silently-ignored no-op - the same failure mode this comment
    // already warns about for case sensitivity, closed for property names as well as casing.
    private static readonly JsonSerializerOptions FixtureOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // PR104: must be kept in sync with the "category" enum in
    // tests/PlaybackCompat/schema/fixture.schema.json - deliberately checked here too (not just by
    // FixtureSchemaValidationTests) so a fixture with a bogus category fails on two independent
    // mechanisms rather than one.
    private static readonly IReadOnlyCollection<string> KnownCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "direct-play", "remux", "audio-transcode", "video-codec-incompatible",
        "bitrate-resolution-limit", "hdr-tonemap", "subtitle-burn-in",
        "subtitle-external", "downmix", "live-tv", "alternate-versions", "no-viable-plan",
        "requested-source", "direct-play-mismatch", "per-codec-limit",
        "output-profile-order", "protocol-selection", "subtitle-auto-select",
        "invalid-index", "codec-bitrate-limit", "av1-preferred",
    };

    [Theory]
    [MemberData(nameof(FixtureCatalog.AllFixtures), MemberType = typeof(FixtureCatalog))]
    public static void Fixture_EngineDecisionMatchesExpected(string fixtureName)
    {
        var fixture = LoadFixture(fixtureName);

        Assert.Equal(5, fixture.FixtureVersion);
        Assert.False(string.IsNullOrEmpty(fixture.Id));
        Assert.Equal(Path.GetFileNameWithoutExtension(fixtureName), fixture.Id);
        Assert.False(string.IsNullOrEmpty(fixture.Category));
        Assert.Contains(fixture.Category, KnownCategories);
        Assert.Equal(PlaybackEngine.EngineVersion, fixture.EngineVersion);

        var engine = new PlaybackEngine();
        var context = new PlaybackRequestContext(
            RequestId: Guid.Empty,
            ItemId: Guid.Empty,
            MediaSourceId: fixture.Input.RequestedMediaSourceId,
            UserId: Guid.Empty,
            MediaKind: fixture.Input.Context.MediaKind,
            RequestedAt: default,
            EngineVersion: fixture.EngineVersion);

        var decision = engine.Decide(context, fixture.Input.Capabilities, fixture.Input.Sources, fixture.Input.Constraints);

        var expected = fixture.Expected;

        Assert.Equal(Enum.Parse<PlaybackMethod>(expected.Method), decision.Method);
        Assert.Equal(expected.IsViable, decision.IsViable);

        Assert.Equal(expected.SelectedStreams.Video, decision.SelectedStreams.Video);
        Assert.Equal(expected.SelectedStreams.Audio, decision.SelectedStreams.Audio);
        Assert.Equal(expected.SelectedStreams.Subtitle, decision.SelectedStreams.Subtitle?.Index);

        // The fixture's `expected.output` only declares the fields relevant to the case it's
        // isolating; an absent field deserializes to null on FixtureOutput, which is exactly what
        // the engine is expected to produce when that field is not applicable/unchanged. Protocol
        // (PR104) is the one exception: it is non-nullable on OutputSpec, and an absent fixture
        // value defaults to StreamingProtocol.Http (enum member 0) on FixtureOutput too, so the
        // comparison is unconditional and still correct for fixtures that never mention it.
        Assert.Equal(expected.Output.Container, decision.Output.Container);
        Assert.Equal(expected.Output.VideoCodec, decision.Output.VideoCodec);
        Assert.Equal(expected.Output.AudioCodec, decision.Output.AudioCodec);
        Assert.Equal(expected.Output.Resolution, decision.Output.Resolution);
        Assert.Equal(expected.Output.VideoRange, decision.Output.VideoRange);
        Assert.Equal(expected.Output.AudioChannels, decision.Output.AudioChannels);
        Assert.Equal(expected.Output.TotalBitrate, decision.Output.TotalBitrate);
        Assert.Equal(expected.Output.VideoBitrate, decision.Output.VideoBitrate);
        Assert.Equal(expected.Output.AudioBitrate, decision.Output.AudioBitrate);
        Assert.Equal(expected.Output.Protocol, decision.Output.Protocol);

        var expectedTransforms = expected.Transforms.Select(Enum.Parse<TransformKind>).ToHashSet();
        var actualTransforms = decision.Transforms.ToHashSet();
        Assert.Equal(expectedTransforms, actualTransforms);

        var expectedReasonCodes = expected.ReasonCodes.Select(Enum.Parse<ReasonCode>).ToHashSet();
        var actualReasonCodes = FlattenReasonCodes(decision.Reasoning).ToHashSet();
        Assert.Equal(expectedReasonCodes, actualReasonCodes);
    }

    internal static FixtureFile LoadFixture(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FixtureFile>(json, FixtureOptions)
            ?? throw new InvalidOperationException($"Fixture '{fixtureName}' deserialized to null.");
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

    internal sealed record FixtureFile(
        int FixtureVersion,
        string Id,
        string Category,
        int EngineVersion,
        string? Description,
        FixtureInput Input,
        FixtureExpected Expected);

    internal sealed record FixtureInput(
        FixtureContext Context,
        ClientCapabilities Capabilities,
        IReadOnlyList<MediaSourceSnapshot> Sources,
        string? RequestedMediaSourceId,
        PlaybackConstraints Constraints);

    /// <summary>
    /// PR104: the fixture's <c>context</c> object now carries only <see cref="MediaKind"/> - the
    /// requested source id lives on <see cref="FixtureInput.RequestedMediaSourceId"/> instead, a
    /// dedicated top-level field rather than a property nested inside a request-identity object.
    /// </summary>
    internal sealed record FixtureContext(MediaKind MediaKind);

    internal sealed record FixtureExpected(
        string Method,
        FixtureSelectedStreams SelectedStreams,
        FixtureOutput Output,
        IReadOnlyList<string> Transforms,
        IReadOnlyList<string> ReasonCodes,
        bool IsViable);

    internal sealed record FixtureSelectedStreams(int? Video, int? Audio, int? Subtitle);

    internal sealed record FixtureOutput(
        string? Container,
        string? VideoCodec,
        string? AudioCodec,
        Resolution? Resolution,
        string? VideoRange,
        int? AudioChannels,
        int? TotalBitrate,
        int? VideoBitrate,
        int? AudioBitrate,
        StreamingProtocol Protocol);
}
