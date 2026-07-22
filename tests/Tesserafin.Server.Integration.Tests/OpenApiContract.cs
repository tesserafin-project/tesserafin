using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// Canonicalisation and fingerprinting of the generated OpenAPI contract.
    ///
    /// <para>
    /// Why canonicalisation is needed: the raw bytes served by
    /// <c>/api-docs/openapi.json</c> are NOT a stable identity for the contract.
    /// Two things vary without the API surface changing at all:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>servers</c> — rewritten per request from the inbound <c>Host</c> header
    /// (<c>Tesserafin.Server/Extensions/ApiApplicationBuilderExtensions.cs</c>, PreSerializeFilter,
    /// and <c>CachingOpenApiProvider.AdjustDocument</c>). It describes where *this*
    /// process happens to be reachable, not what the API is, so it is dropped.
    /// </description></item>
    /// <item><description>
    /// Member order inside JSON objects — an emission-order artefact of schema
    /// generation, not contract content. Every object's keys are re-emitted in
    /// ordinal order. Array order IS preserved: in OpenAPI, arrays (parameters,
    /// enum values, required) are semantically ordered.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// No timestamp is stripped here because the produced document contains none —
    /// see <c>docs/openapi-contract.md</c> §"Horodatage". The only <c>generatedAt</c>
    /// in the repo is a runtime response field
    /// (<c>Tesserafin.Api/Models/PlaybackSessionDtos/PlaybackOperationalMetricsResponse.cs</c>),
    /// which appears in the spec purely as a schema property *name* — stable by
    /// construction.
    /// </para>
    /// </summary>
    internal static class OpenApiContract
    {
        /// <summary>
        /// Repo-relative path of the committed canonical contract.
        /// </summary>
        public const string SpecRelativePath = "openapi/openapi.json";

        /// <summary>
        /// Repo-relative path of the committed contract fingerprint sidecar.
        /// </summary>
        public const string LockRelativePath = "openapi/contract.lock.json";

        /// <summary>
        /// Environment variable that switches the contract test from "verify" to "regenerate".
        /// Set by <c>ci/openapi-generate.sh</c> only.
        /// </summary>
        public const string WriteEnvironmentVariable = "TESSERAFIN_OPENAPI_WRITE";

        /// <summary>
        /// The exact command a developer must run to refresh the committed contract.
        /// </summary>
        public const string RegenerateCommand = "./ci/openapi-generate.sh";

        /// <summary>
        /// Every knob that could make the output machine- or locale-dependent is pinned
        /// explicitly rather than left to its default: <c>NewLine</c> defaults to
        /// <c>Environment.NewLine</c> (CRLF on Windows) and the indent character/size
        /// defaults are framework-version dependent.
        /// </summary>
        private static readonly JsonWriterOptions _writerOptions = new()
        {
            Indented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            NewLine = "\n",
            SkipValidation = false,
        };

        /// <summary>
        /// Turns a raw OpenAPI JSON document into its canonical byte form.
        /// Deterministic: equal API surface in, equal bytes out.
        /// </summary>
        /// <param name="rawJson">The document as served by the running server.</param>
        /// <returns>Canonical UTF-8 bytes, LF-terminated.</returns>
        public static byte[] Canonicalize(string rawJson)
        {
            using var document = JsonDocument.Parse(rawJson);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
            {
                WriteCanonical(writer, document.RootElement, isRoot: true);
            }

            // A trailing newline: POSIX text-file convention, and it keeps `git diff`
            // from reporting "\ No newline at end of file" on every contract change.
            buffer.Write("\n"u8);
            return buffer.ToArray();
        }

        /// <summary>
        /// Computes the contract fingerprint.
        ///
        /// <para>
        /// The fingerprint is deliberately NOT written into the OpenAPI document: hashing a
        /// document that already carries its own hash is circular (writing the hash in
        /// changes the bytes, which changes the hash). It lives in a separate sidecar
        /// (<see cref="LockRelativePath"/>) whose input is exclusively the canonical bytes of
        /// <see cref="SpecRelativePath"/>. The spec never references the sidecar, so the
        /// dependency is a one-way edge: spec -> hash -> sidecar.
        /// </para>
        /// </summary>
        /// <param name="canonicalBytes">Output of <see cref="Canonicalize"/>.</param>
        /// <returns>Lowercase hex SHA-256.</returns>
        public static string Fingerprint(byte[] canonicalBytes)
        {
            return Convert.ToHexStringLower(SHA256.HashData(canonicalBytes));
        }

        /// <summary>
        /// Reads <c>info.version</c> out of a contract document.
        /// </summary>
        /// <param name="canonicalBytes">Output of <see cref="Canonicalize"/>.</param>
        /// <returns>The declared contract version.</returns>
        public static string ReadInfoVersion(byte[] canonicalBytes)
        {
            using var document = JsonDocument.Parse(canonicalBytes);
            return document.RootElement.GetProperty("info").GetProperty("version").GetString()
                ?? throw new InvalidOperationException("info.version is null in the generated contract.");
        }

        /// <summary>
        /// Builds the sidecar that pins a contract by version + fingerprint.
        ///
        /// <para>
        /// Contains no generation timestamp on purpose. A timestamp would change on every
        /// regeneration even when the contract is byte-identical, which is precisely the
        /// drift signal this file exists to carry — it would make the drift gate fire on
        /// no change at all. Nothing consumes such a field: the only pin consumer is
        /// <c>tesserafin-web/src/lib/tesserafin-sdk/spec/version.json</c>, which pins on version
        /// and hash. See <c>docs/openapi-contract.md</c>.
        /// </para>
        /// </summary>
        /// <param name="version">The contract version (server assembly version).</param>
        /// <param name="fingerprint">Output of <see cref="Fingerprint"/>.</param>
        /// <returns>Canonical UTF-8 bytes for the sidecar, LF-terminated.</returns>
        public static byte[] BuildLockFile(string version, string fingerprint)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("algorithm", "sha256");
                writer.WriteString("sha256", fingerprint);
                writer.WriteString("spec", SpecRelativePath);
                writer.WriteString("version", version);
                writer.WriteEndObject();
            }

            buffer.Write("\n"u8);
            return buffer.ToArray();
        }

        /// <summary>
        /// Locates the repository root by walking up from the test assembly until
        /// <c>Tesserafin.sln</c> is found. The test binaries live under
        /// <c>tests/&lt;project&gt;/bin/&lt;config&gt;/&lt;tfm&gt;/</c>, so the depth is fixed but
        /// configuration-dependent; searching for the marker avoids hard-coding it.
        /// </summary>
        /// <returns>Absolute path to the repository root.</returns>
        public static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                FormattableString.Invariant($"Could not locate Tesserafin.sln walking up from '{AppContext.BaseDirectory}'."));
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool isRoot = false)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    IEnumerable<JsonProperty> properties = element.EnumerateObject()
                        .OrderBy(p => p.Name, StringComparer.Ordinal);
                    if (isRoot)
                    {
                        properties = properties.Where(p => !string.Equals(p.Name, "servers", StringComparison.Ordinal));
                    }

                    foreach (var property in properties)
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonical(writer, property.Value);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteCanonical(writer, item);
                    }

                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                    writer.WriteStringValue(element.GetString());
                    break;

                case JsonValueKind.Number:
                    // Re-emit the raw token: round-tripping through double would silently
                    // reformat integers and lose precision on large values.
                    writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                    break;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    writer.WriteBooleanValue(element.GetBoolean());
                    break;

                case JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;

                default:
                    throw new InvalidOperationException(
                        string.Create(CultureInfo.InvariantCulture, $"Unexpected JSON value kind '{element.ValueKind}' in the OpenAPI document."));
            }
        }

        /// <summary>
        /// Builds the failure text shown when the committed contract no longer matches
        /// what the server produces. Deliberately verbose: this fires inside
        /// <c>./ci/run.sh</c>, the mandatory merge gate, where the reader needs to know
        /// what to run without leaving the terminal.
        /// </summary>
        /// <param name="expectedFingerprint">Fingerprint of the committed contract.</param>
        /// <param name="actualFingerprint">Fingerprint of the freshly generated contract.</param>
        /// <returns>The failure message.</returns>
        public static string BuildDriftMessage(string expectedFingerprint, string actualFingerprint)
        {
            var message = new StringBuilder();
            message.Append("The committed OpenAPI contract is out of date.\n\n");
            message.Append(CultureInfo.InvariantCulture, $"  committed {SpecRelativePath} : sha256 {expectedFingerprint}\n");
            message.Append(CultureInfo.InvariantCulture, $"  server produces           : sha256 {actualFingerprint}\n\n");
            message.Append("You changed the API surface (a controller, a DTO, an attribute, a status code).\n");
            message.Append("The contract is committed, so it has to be regenerated in the SAME commit:\n\n");
            message.Append(CultureInfo.InvariantCulture, $"    {RegenerateCommand}\n\n");
            message.Append(CultureInfo.InvariantCulture, $"then commit the updated {SpecRelativePath} and {LockRelativePath}.\n");
            message.Append("If you did NOT intend to change the API surface, the diff on those files tells\n");
            message.Append("you what you actually changed — read it before regenerating.\n");
            return message.ToString();
        }
    }
}
