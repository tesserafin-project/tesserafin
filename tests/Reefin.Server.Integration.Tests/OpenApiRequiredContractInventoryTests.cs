using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Reefin.Server.Integration.Tests
{
    /// <summary>
    /// Pins the complete effect of <c>PlaybackDecisionRequiredSchemaFilter</c> on the published
    /// contract (issue #51).
    ///
    /// <para>
    /// The expected sets below are written out by hand from the C# declarations in
    /// <c>src/Reefin.Playback.Decision/</c>, deliberately <b>not</b> derived by re-running the
    /// filter's own rule. A test that recomputed the rule would pass for any rule; this one fails
    /// if the filter's behaviour ever stops matching the reviewed inventory in
    /// <c>docs/pr-openapi-required-audit.md</c> §3 (approach C: 29 entries, 13 schemas).
    /// </para>
    /// </summary>
    public sealed class OpenApiRequiredContractInventoryTests
    {
        private const string SchemaPrefix = "PlaybackDecision";

        /// <summary>
        /// Number of schemas outside the <c>Reefin.Playback.Decision</c> namespace that declare
        /// <c>required</c>. All of them come from an explicit <c>[Required]</c> attribute and none
        /// is in this change's scope, so the filter must leave this figure untouched.
        /// </summary>
        private const int RequiredOutsideNamespace = 27;

        /// <summary>
        /// The 29 members, across 13 schemas, that are primary-constructor parameters of
        /// non-nullable reference type with no default value - and therefore the exact set the
        /// server answers 400 for when absent.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string[]> _expectedRequired =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["PlaybackDecisionAudioCodecCapability"] = ["Codec"],
                ["PlaybackDecisionAudioStreamSnapshot"] = ["Codec"],
                ["PlaybackDecisionClientCapabilities"] = ["Decode", "OutputProfiles"],
                ["PlaybackDecisionDecodeCapabilities"] = ["AudioCodecs", "DirectPlayProfiles", "SubtitleDelivery", "VideoCodecs"],
                ["PlaybackDecisionDecodeProfile"] = ["AudioCodecs", "Containers", "VideoCodecs"],
                ["PlaybackDecisionMediaSourceSnapshot"] = ["AudioStreams", "Container", "MediaSourceId", "Protocol", "SubtitleStreams", "VideoStreams"],
                ["PlaybackDecisionPlaybackConstraints"] = ["PreferredSubtitleLanguages"],
                ["PlaybackDecisionPlaybackOutputProfile"] = ["AudioCodecs", "Container", "VideoCodecs"],
                ["PlaybackDecisionReasonNode"] = ["Children", "Subject"],
                ["PlaybackDecisionSubtitleCapability"] = ["Format"],
                ["PlaybackDecisionSubtitleStreamSnapshot"] = ["Format"],
                ["PlaybackDecisionVideoCodecCapability"] = ["Codec", "Profiles", "VideoRangeTypes"],
                ["PlaybackDecisionVideoStreamSnapshot"] = ["Codec"],
            };

        /// <summary>
        /// The object schemas of the same namespace that must carry <b>no</b> <c>required</c> array,
        /// because every one of their primary-constructor parameters is either a value type or
        /// nullable. Listing them explicitly is what makes the inventory above exhaustive: a schema
        /// silently skipped by the filter would show up here rather than go unnoticed.
        /// </summary>
        private static readonly string[] _expectedWithoutRequired =
        [
            "PlaybackDecisionOutputSpec",
            "PlaybackDecisionPlaybackRequestContext",
            "PlaybackDecisionReasonSubject",
            "PlaybackDecisionResolution",
            "PlaybackDecisionSelectedStreams",
            "PlaybackDecisionSelectedSubtitle",
        ];

        [Fact]
        public void PlaybackDecisionSchemas_DeclareExactlyTheProvablyRequiredMembers()
        {
            var schemas = ReadSchemas();

            var actual = schemas.EnumerateObject()
                .Where(p => p.Name.StartsWith(SchemaPrefix, StringComparison.Ordinal))
                .Where(p => p.Value.TryGetProperty("required", out _))
                .ToDictionary(
                    p => p.Name,
                    p => p.Value.GetProperty("required")
                        .EnumerateArray()
                        .Select(e => e.GetString())
                        .OrderBy(e => e, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);

            Assert.Equal(
                _expectedRequired.Keys.OrderBy(k => k, StringComparer.Ordinal),
                actual.Keys.OrderBy(k => k, StringComparer.Ordinal));

            foreach (var (schemaId, expectedMembers) in _expectedRequired)
            {
                Assert.Equal(expectedMembers.OrderBy(m => m, StringComparer.Ordinal), actual[schemaId]);
            }

            Assert.Equal(29, actual.Sum(entry => entry.Value.Length));
            Assert.Equal(13, actual.Count);
        }

        [Fact]
        public void PlaybackDecisionSchemas_WithoutAProvableMember_DeclareNoRequired()
        {
            var schemas = ReadSchemas();

            foreach (var schemaId in _expectedWithoutRequired)
            {
                Assert.True(
                    schemas.TryGetProperty(schemaId, out var schema),
                    FormattableString.Invariant($"Schema '{schemaId}' is absent from {OpenApiContract.SpecRelativePath}."));

                Assert.False(
                    schema.TryGetProperty("required", out _),
                    FormattableString.Invariant(
                        $"{schemaId} declares 'required', but every primary-constructor parameter it has is a value type or nullable, so the server accepts them all absent."));
            }
        }

        /// <summary>
        /// The control the whole approach rests on: members the server accepts absent must not be
        /// declared required. A value type binds to its default and a nullable member binds to
        /// <see langword="null"/>; in neither case does MVC's implicit <c>[Required]</c> fire.
        /// </summary>
        /// <param name="schemaId">The schema to inspect.</param>
        /// <param name="propertyName">The member that must stay optional.</param>
        [Theory]
        // Non-nullable value types (bool/enum): absent yields false / the zero enum member.
        [InlineData("PlaybackDecisionDecodeCapabilities", "SupportsHls")]
        [InlineData("PlaybackDecisionDecodeCapabilities", "SupportsDash")]
        [InlineData("PlaybackDecisionDecodeProfile", "Type")]
        [InlineData("PlaybackDecisionPlaybackOutputProfile", "Protocol")]
        [InlineData("PlaybackDecisionPlaybackConstraints", "AllowDirectPlay")]
        [InlineData("PlaybackDecisionPlaybackConstraints", "StartTimeTicks")]
        [InlineData("PlaybackDecisionAudioStreamSnapshot", "Index")]
        [InlineData("PlaybackDecisionSubtitleStreamSnapshot", "IsForced")]
        [InlineData("PlaybackDecisionVideoStreamSnapshot", "IsAnamorphic")]
        // Nullable reference / value types: absent and null are indistinguishable, both accepted.
        [InlineData("PlaybackDecisionVideoCodecCapability", "MaxLevel")]
        [InlineData("PlaybackDecisionVideoCodecCapability", "MaxBitDepth")]
        [InlineData("PlaybackDecisionVideoCodecCapability", "MaxResolution")]
        [InlineData("PlaybackDecisionVideoCodecCapability", "MaxBitrate")]
        [InlineData("PlaybackDecisionMediaSourceSnapshot", "RunTimeTicks")]
        [InlineData("PlaybackDecisionReasonNode", "Detail")]
        [InlineData("PlaybackDecisionAudioStreamSnapshot", "Language")]
        [InlineData("PlaybackDecisionVideoStreamSnapshot", "Profile")]
        public void MembersTheServerAcceptsAbsent_AreNotRequired(string schemaId, string propertyName)
        {
            var schemas = ReadSchemas();

            Assert.True(
                schemas.TryGetProperty(schemaId, out var schema),
                FormattableString.Invariant($"Schema '{schemaId}' is absent from {OpenApiContract.SpecRelativePath}."));

            Assert.True(
                schema.GetProperty("properties").TryGetProperty(propertyName, out _),
                FormattableString.Invariant($"{schemaId} has no property '{propertyName}' at all."));

            var required = schema.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(e => e.GetString()).ToList()
                : [];

            Assert.False(
                required.Contains(propertyName, StringComparer.Ordinal),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{schemaId}.{propertyName} is declared required, but the server accepts it absent - it is a value type or a nullable member, so MVC's implicit [Required] never fires. Declaring it required would break clients that legitimately omit it."));
        }

        /// <summary>
        /// The filter is scoped to one namespace by exact match. Sibling vocabularies and every
        /// other schema in the document must be byte-unaffected, so the population of schemas that
        /// declare <c>required</c> outside the namespace stays exactly what explicit
        /// <c>[Required]</c> attributes produced.
        /// </summary>
        [Fact]
        public void SchemasOutsideTheNamespace_KeepTheirRequiredArraysUnchanged()
        {
            var schemas = ReadSchemas();

            var outside = schemas.EnumerateObject()
                .Where(p => !p.Name.StartsWith(SchemaPrefix, StringComparison.Ordinal))
                .Count(p => p.Value.TryGetProperty("required", out _));

            Assert.Equal(RequiredOutsideNamespace, outside);
        }

        private static JsonElement ReadSchemas()
        {
            var specPath = Path.Combine(
                OpenApiContract.FindRepositoryRoot(),
                OpenApiContract.SpecRelativePath);

            Assert.True(
                File.Exists(specPath),
                FormattableString.Invariant(
                    $"{OpenApiContract.SpecRelativePath} is missing. Generate it with: {OpenApiContract.RegenerateCommand}"));

            using var document = JsonDocument.Parse(File.ReadAllBytes(specPath));

            // JsonDocument owns the backing buffer, so hand back a detached copy.
            return document.RootElement.GetProperty("components").GetProperty("schemas").Clone();
        }
    }
}
