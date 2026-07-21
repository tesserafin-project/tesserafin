using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Shadow;
using Xunit;

namespace Tesserafin.Api.Tests.Models.PlaybackSessionDtos;

/// <summary>
/// Tests for <see cref="PlaybackCompatFixtureExporter"/> (PR113b): the produced JSON must actually
/// conform to tests/PlaybackCompat/schema/fixture.schema.json - the same structural gate
/// <c>Tesserafin.Playback.Engine.Tests.FixtureSchemaValidationTests</c> runs for the lab's own curated
/// fixtures - not merely resemble it.
/// </summary>
public sealed class PlaybackCompatFixtureExporterTests
{
    // Parsed once: Json.Schema registers a schema globally by its $id when built, so calling
    // JsonSchema.FromText repeatedly for the same $id throws "Overwriting registered schemas is
    // not permitted." (same reasoning as FixtureSchemaValidationTests, this is a different process
    // so does not collide with that project's own registration).
    private static readonly JsonSchema Schema = LoadSchema();
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    [Fact]
    public void Export_SerializedFixture_ValidatesAgainstCompatibilityLabSchema()
    {
        var id = PlaybackSessionId.NewId();
        var diagnostic = CreateDirectPlayRecordWithSource();

        var export = PlaybackCompatFixtureExporter.Export(id, diagnostic);
        var json = PlaybackCompatFixtureExporter.ToJson(export);

        using var document = JsonDocument.Parse(json);
        var results = Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        if (!results.IsValid)
        {
            var report = JsonSerializer.Serialize(results, ReportOptions);
            Assert.Fail($"Export failed schema validation:{Environment.NewLine}{report}{Environment.NewLine}JSON:{Environment.NewLine}{json}");
        }
    }

    [Fact]
    public void Export_Id_MatchesSchemaPatternAndIsDeterministicFromSessionId()
    {
        var id = PlaybackSessionId.NewId();
        var diagnostic = CreateDirectPlayRecordWithSource();

        var export = PlaybackCompatFixtureExporter.Export(id, diagnostic);

        Assert.Matches("^[a-z0-9-]+$", export.Id);
        Assert.Equal($"exported-{id.Value:N}", export.Id);
    }

    [Fact]
    public void Export_FixtureVersion_IsCurrentSchemaVersion()
    {
        var diagnostic = CreateDirectPlayRecordWithSource();

        var export = PlaybackCompatFixtureExporter.Export(PlaybackSessionId.NewId(), diagnostic);

        Assert.Equal(5, export.FixtureVersion);
    }

    [Fact]
    public void Export_NonViableDecision_CategoryIsNoViablePlan()
    {
        var diagnostic = CreateDirectPlayRecordWithSource() with
        {
            Decision = PlaybackDecision.NotViable(
                PlaybackMethod.Transcode,
                ReasonNode.Leaf(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method()),
                engineVersion: 6),
        };

        var export = PlaybackCompatFixtureExporter.Export(PlaybackSessionId.NewId(), diagnostic);

        Assert.Equal("no-viable-plan", export.Category);
        Assert.False(export.Expected.IsViable);
    }

    [Fact]
    public void Export_DirectPlayDecision_CategoryIsDirectPlay()
    {
        var diagnostic = CreateDirectPlayRecordWithSource();

        var export = PlaybackCompatFixtureExporter.Export(PlaybackSessionId.NewId(), diagnostic);

        Assert.Equal("direct-play", export.Category);
    }

    /// <summary>
    /// The fixture schema's <c>constraints</c> object has no <c>startTimeTicks</c> property
    /// (<c>additionalProperties:false</c>) - the exporter must omit it, not just leave it null,
    /// since <c>PlaybackConstraints.StartTimeTicks</c> is a non-nullable <see cref="long"/> that
    /// would otherwise always serialize as <c>0</c>, an unknown/rejected property under the schema.
    /// </summary>
    [Fact]
    public void Export_SerializedJson_NeverContainsStartTimeTicks()
    {
        var diagnostic = CreateDirectPlayRecordWithSource();

        var json = PlaybackCompatFixtureExporter.ToJson(PlaybackCompatFixtureExporter.Export(PlaybackSessionId.NewId(), diagnostic));

        Assert.DoesNotContain("startTimeTicks", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fixture schema's <c>expected.output</c> object has no <c>subtitleFormat</c> property.
    /// </summary>
    [Fact]
    public void Export_SerializedJson_NeverContainsSubtitleFormat()
    {
        var diagnostic = CreateDirectPlayRecordWithSource();

        var json = PlaybackCompatFixtureExporter.ToJson(PlaybackCompatFixtureExporter.Export(PlaybackSessionId.NewId(), diagnostic));

        Assert.DoesNotContain("subtitleFormat", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The schema's <c>expected.selectedStreams.subtitle</c> is a plain stream index, not the
    /// <see cref="SelectedSubtitle"/> object (index + delivery method) the domain decision carries.
    /// </summary>
    [Fact]
    public void Export_SelectedSubtitle_ExportsIndexOnlyNotDeliveryMethod()
    {
        var diagnostic = CreateDirectPlayRecordWithSource() with
        {
            Decision = PlaybackDecision.Remux(
                "src-1",
                new SelectedStreams(0, 1, new SelectedSubtitle(2, SubtitleDeliveryMethod.Embed)),
                new OutputSpec("mkv", "h264", "aac", null, null, null, null, null, null, StreamingProtocol.Http, "srt"),
                [TransformKind.RemuxContainer],
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: 6),
        };

        var export = PlaybackCompatFixtureExporter.Export(PlaybackSessionId.NewId(), diagnostic);
        var json = PlaybackCompatFixtureExporter.ToJson(export);

        Assert.Equal(2, export.Expected.SelectedStreams.Subtitle);
        Assert.DoesNotContain("\"delivery\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJson_UsesCamelCasePropertyNames()
    {
        var diagnostic = CreateDirectPlayRecordWithSource();

        var json = PlaybackCompatFixtureExporter.ToJson(PlaybackCompatFixtureExporter.Export(PlaybackSessionId.NewId(), diagnostic));

        Assert.Contains("\"fixtureVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"engineVersion\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FixtureVersion\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Enum VALUES (as opposed to object property names) keep their PascalCase C# member spelling -
    /// the schema's own convention (for example <c>"method": "DirectPlay"</c>), the opposite
    /// casing from property names.
    /// </summary>
    [Fact]
    public void ToJson_EnumValues_KeepPascalCaseSpelling()
    {
        var diagnostic = CreateDirectPlayRecordWithSource();

        var json = PlaybackCompatFixtureExporter.ToJson(PlaybackCompatFixtureExporter.Export(PlaybackSessionId.NewId(), diagnostic));

        Assert.Matches(new Regex("\"method\"\\s*:\\s*\"DirectPlay\""), json);
    }

    private static ShadowDiagnosticRecord CreateDirectPlayRecordWithSource()
    {
        var source = new MediaSourceSnapshot(
            MediaSourceId: "src-1",
            Container: "mp4",
            Protocol: "http",
            Bitrate: 8_000_000,
            RunTimeTicks: 100_000_000,
            VideoStreams: [new VideoStreamSnapshot(0, "h264", "high", 40, 1920, 1080, 8, "SDR", 24.0, 7_000_000, false, false)],
            AudioStreams: [new AudioStreamSnapshot(1, "aac", 2, 48000, null, null, null, true)],
            SubtitleStreams: [],
            SupportsDirectPlay: true,
            SupportsDirectStream: true,
            SupportsTranscoding: true);

        return new ShadowDiagnosticRecord(
            Decision: PlaybackDecision.DirectPlay(
                "src-1",
                new SelectedStreams(0, 1, null),
                new OutputSpec("mp4", "h264", "aac", new Resolution(1920, 1080), "SDR", 2, null, null, null, StreamingProtocol.Http, null),
                ReasonNode.Leaf(ReasonCode.MethodChosen, ReasonOutcome.Chosen, ReasonSubject.Method()),
                engineVersion: 6),
            LegacyVector: new DecisionVector(
                IsViable: true,
                Method: NormalizedMethod.DirectPlay,
                VideoStreamIndex: StreamSelection.Selected(0),
                AudioStreamIndex: StreamSelection.Selected(1),
                SubtitleStreamIndex: StreamSelection.None,
                TransformClasses: new HashSet<TransformClass>(),
                ReasonCategories: new HashSet<ReasonCategory>(),
                OutputContainer: "mp4",
                OutputVideoCodec: "h264",
                OutputAudioCodec: "aac",
                SelectedSource: "src-1",
                OutputWidth: 1920,
                OutputHeight: 1080,
                OutputBitrate: null,
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
                MediaSourceId: "src-1",
                UserId: Guid.NewGuid(),
                MediaKind: MediaKind.Video,
                RequestedAt: DateTimeOffset.UtcNow,
                EngineVersion: 6),
            Capabilities: new ClientCapabilities(
                Decode: new DecodeCapabilities(
                    DirectPlayProfiles: [new DecodeProfile(MediaKind.Video, ["mp4"], ["h264"], ["aac"])],
                    VideoCodecs: [new VideoCodecCapability("h264", ["high"], 41, 8, ["SDR"], new Resolution(1920, 1080), 20_000_000)],
                    AudioCodecs: [new AudioCodecCapability("aac", 6, 48000, null, null)],
                    SubtitleDelivery: [],
                    SupportsHls: true,
                    SupportsDash: false),
                OutputProfiles: []),
            Sources: [source],
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
                StartTimeTicks: 12345),
            Kind: PlaybackMediaKind.Video,
            CapturedAt: DateTimeOffset.UtcNow);
    }

    private static JsonSchema LoadSchema()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "schema", "fixture.schema.json");
        return JsonSchema.FromText(File.ReadAllText(schemaPath));
    }
}
