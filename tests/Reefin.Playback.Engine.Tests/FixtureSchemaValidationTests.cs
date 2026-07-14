using System;
using System.IO;
using System.Text.Json;
using Json.Schema;
using Xunit;

namespace Reefin.Playback.Engine.Tests;

/// <summary>
/// Validates every fixture in tests/PlaybackCompat/fixtures/ against
/// tests/PlaybackCompat/schema/fixture.schema.json (PR104): the structural gate (required
/// properties, enum membership, additionalProperties:false) that PR93 designed but never wired to
/// an executable validator - the schema was documentation-only until this class. Complements
/// <see cref="FixtureParityTests"/> (the behavioral gate): a fixture can be well-typed C# (passing
/// strict deserialization) yet still violate the schema's stricter shape rules (extra JSON property
/// nested inside an object the top-level deserializer target doesn't itself reject the same way,
/// wrong category, etc.) - this class is what actually enforces the schema document is true.
/// </summary>
public static class FixtureSchemaValidationTests
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    // Parsed once: Json.Schema registers a schema globally by its $id when built, so calling
    // JsonSchema.FromText repeatedly for the same $id (once per [Theory] case) throws
    // "Overwriting registered schemas is not permitted."
    private static readonly JsonSchema Schema = LoadSchema();

    [Theory]
    [MemberData(nameof(FixtureCatalog.AllFixtures), MemberType = typeof(FixtureCatalog))]
    public static void Fixture_ValidatesAgainstSchema(string fixtureName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName);
        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));

        var results = Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        if (!results.IsValid)
        {
            var report = JsonSerializer.Serialize(results, ReportOptions);
            Assert.Fail($"Fixture '{fixtureName}' failed schema validation:{Environment.NewLine}{report}");
        }
    }

    /// <summary>
    /// A deliberately malformed fixture (unknown TOP-LEVEL property) must be REJECTED. This alone
    /// only proves the root object's own <c>additionalProperties:false</c> fires - see
    /// <see cref="FixtureWithUnknownNestedSourceProperty_FailsSchemaValidation"/> and
    /// <see cref="FixtureWithMissingRequiredNestedSourceProperty_FailsSchemaValidation"/> for the
    /// nested-<c>$ref</c> guards that close the rest of the "schema silently validates everything"
    /// failure mode (the exact way the pre-PR104 draft-07-declared-but-2019-09-shaped schema stayed
    /// documentation-only even had it been wired to a validator).
    /// </summary>
    [Fact]
    public static void MalformedFixture_FailsSchemaValidation()
    {
        var validFixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", FixtureCatalog.AllFixtureNames[0]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(validFixturePath))!.AsObject();
        node["thisPropertyDoesNotExist"] = "sabotage";
        var element = JsonSerializer.SerializeToElement(node);

        var results = Schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(results.IsValid);
    }

    /// <summary>
    /// A deliberately malformed fixture with an unknown property NESTED inside a <c>sources[]</c>
    /// entry (resolved via <c>#/$defs/mediaSource</c>) must be REJECTED. The root-level sabotage
    /// above only proves the root object's own <c>additionalProperties:false</c> fires - it says
    /// nothing about whether <c>$ref</c>/<c>$defs</c> actually resolve under 2020-12 (versus being
    /// silently treated as always-valid, which is exactly how the old draft-07 declaration with
    /// <c>$defs</c> - 2019-09/2020-12 vocabulary the schema never actually spoke - stayed
    /// documentation-only even had it been wired to a validator). This is the test that actually
    /// proves the nested schema enforces something.
    /// </summary>
    [Fact]
    public static void FixtureWithUnknownNestedSourceProperty_FailsSchemaValidation()
    {
        var validFixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", FixtureCatalog.AllFixtureNames[0]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(validFixturePath))!.AsObject();
        node["input"]!["sources"]![0]!["thisPropertyDoesNotExistOnMediaSource"] = "sabotage";
        var element = JsonSerializer.SerializeToElement(node);

        var results = Schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(results.IsValid);
    }

    /// <summary>
    /// A deliberately malformed fixture missing the REQUIRED nested <c>container</c> property on a
    /// <c>sources[]</c> entry must be REJECTED - proves <c>$defs/mediaSource</c>'s own
    /// <c>required</c> list is enforced, not just its <c>additionalProperties:false</c>.
    /// </summary>
    [Fact]
    public static void FixtureWithMissingRequiredNestedSourceProperty_FailsSchemaValidation()
    {
        var validFixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", FixtureCatalog.AllFixtureNames[0]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(validFixturePath))!.AsObject();
        var removed = node["input"]!["sources"]![0]!.AsObject().Remove("container");
        Assert.True(removed, "Precondition: the fixture's first source must declare 'container' for this test to sabotage anything.");
        var element = JsonSerializer.SerializeToElement(node);

        var results = Schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(results.IsValid);
    }

    private static JsonSchema LoadSchema()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "schema", "fixture.schema.json");
        return JsonSchema.FromText(File.ReadAllText(schemaPath));
    }
}
