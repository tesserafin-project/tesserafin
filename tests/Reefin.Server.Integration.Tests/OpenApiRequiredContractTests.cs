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
    /// Guards that members the server genuinely requires are declared <c>required</c> in the
    /// published contract.
    ///
    /// <para>
    /// This asserts against the committed <c>openapi/openapi.json</c> rather than a freshly
    /// booted server on purpose. <c>OpenApiContractTests.CommittedContract_MatchesRunningServer</c>
    /// already pins committed == generated, so the committed file is a faithful stand-in; reading
    /// it directly keeps this test free of <c>ReefinApplicationFactory</c>, so a boot failure can
    /// never masquerade as a contract failure (or vice versa).
    /// </para>
    ///
    /// <para>
    /// Why these two members: <c>Reefin.Playback.Decision.VideoCodecCapability</c> is a positional
    /// record. <c>Profiles</c> and <c>VideoRangeTypes</c> are non-nullable
    /// <c>IReadOnlyList&lt;string&gt;</c> primary-constructor parameters with no default value, so
    /// an absent JSON member deserializes to <see langword="null"/>, and MVC's implicit
    /// <c>[Required]</c> for non-nullable reference types (<c>MvcOptions
    /// .SuppressImplicitRequiredAttributeForNonNullableReferenceTypes</c> is left at its
    /// <see langword="false"/> default) then rejects the request with 400. The schema reaches a
    /// request body through <c>CreatePlaybackSessionRequest</c> / <c>ReplacePlaybackSessionRequest</c>
    /// -&gt; <c>PlaybackDecisionClientCapabilities</c> -&gt; <c>PlaybackDecisionDecodeCapabilities</c>,
    /// so a client that trusts the contract and omits them gets a 400 it had no way to predict.
    /// See <c>docs/pr-openapi-required-audit.md</c> (issue #51).
    /// </para>
    /// </summary>
    public sealed class OpenApiRequiredContractTests
    {
        private const string VideoCodecCapabilitySchemaId = "PlaybackDecisionVideoCodecCapability";

        /// <summary>
        /// The members of <see cref="VideoCodecCapabilitySchemaId"/> the server rejects a request
        /// for omitting, and which must therefore be declared required.
        /// </summary>
        /// <param name="propertyName">The schema property name.</param>
        [Theory]
        [InlineData("Profiles")]
        [InlineData("VideoRangeTypes")]
        public void VideoCodecCapability_DeclaresServerRequiredMember_AsRequired(string propertyName)
        {
            var schema = ReadSchema(VideoCodecCapabilitySchemaId);

            Assert.True(
                schema.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty(propertyName, out _),
                FormattableString.Invariant(
                    $"{VideoCodecCapabilitySchemaId} has no property '{propertyName}' at all."));

            var required = schema.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(e => e.GetString()).ToList()
                : new List<string?>();

            Assert.True(
                required.Contains(propertyName, StringComparer.Ordinal),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{VideoCodecCapabilitySchemaId}.{propertyName} is a non-nullable primary-constructor parameter with no default, so the server answers 400 when it is absent, but the contract declares it optional. Declared required: [{string.Join(", ", required)}]."));
        }

        private static JsonElement ReadSchema(string schemaId)
        {
            var specPath = Path.Combine(
                OpenApiContract.FindRepositoryRoot(),
                OpenApiContract.SpecRelativePath);

            Assert.True(
                File.Exists(specPath),
                FormattableString.Invariant(
                    $"{OpenApiContract.SpecRelativePath} is missing. Generate it with: {OpenApiContract.RegenerateCommand}"));

            using var document = JsonDocument.Parse(File.ReadAllBytes(specPath));
            var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

            Assert.True(
                schemas.TryGetProperty(schemaId, out var schema),
                FormattableString.Invariant($"Schema '{schemaId}' is absent from {OpenApiContract.SpecRelativePath}."));

            // JsonDocument owns the backing buffer, so hand back a detached copy.
            return schema.Clone();
        }
    }
}
