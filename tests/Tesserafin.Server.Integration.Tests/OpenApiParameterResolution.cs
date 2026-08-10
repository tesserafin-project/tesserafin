using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// Resolves an OpenAPI document the way a conforming consumer must, so contract assertions test
    /// what a generated client actually sees rather than what the committed JSON happens to spell.
    ///
    /// <para>
    /// Issue #226 was invisible to a text search: the five defective path parameters carried
    /// <c>{"allOf": [{"$ref": "…/PlaybackSessionId"}]}</c>, and the word <c>object</c> appeared only
    /// in a component two indirections away. A test that greps the committed bytes for expected
    /// snippets would have passed on the broken document and would pass again on the next variant of
    /// the same defect. Everything here therefore follows <c>$ref</c> and flattens <c>allOf</c>
    /// before asserting.
    /// </para>
    ///
    /// <para>
    /// <c>$ref</c> resolution follows OpenAPI 3.0 semantics: a schema object containing <c>$ref</c>
    /// has all its other members ignored, so the reference replaces the object rather than merging
    /// with it. <c>allOf</c> members are merged in document order and then overridden by the
    /// containing schema's own keywords, which is how Swashbuckle's
    /// <c>UseAllOfToExtendReferenceSchemas()</c> output is meant to be read.
    /// </para>
    /// </summary>
    internal static class OpenApiParameterResolution
    {
        /// <summary>
        /// The HTTP methods an OpenAPI Path Item Object may carry. Everything else under a path
        /// (<c>parameters</c>, <c>summary</c>, <c>$ref</c>, extensions) is not an operation.
        /// </summary>
        private static readonly string[] _operationKeys =
        [
            "get", "put", "post", "delete", "options", "head", "patch", "trace"
        ];

        /// <summary>
        /// Reads and parses the committed contract.
        ///
        /// <para>
        /// Reading the committed file rather than booting a server is sound because
        /// <c>OpenApiContractTests.CommittedContract_MatchesRunningServer</c> already pins committed
        /// == generated on every run of the merge gate; see
        /// <see cref="OpenApiParameterWireShapeLiveTests"/> for the assertion that the same
        /// invariants hold in the document a freshly booted server produces.
        /// </para>
        /// </summary>
        /// <returns>The parsed document. The caller owns it.</returns>
        internal static JsonDocument ReadCommittedContract()
        {
            var specPath = Path.Combine(
                OpenApiContract.FindRepositoryRoot(),
                OpenApiContract.SpecRelativePath);

            if (!File.Exists(specPath))
            {
                throw new FileNotFoundException(
                    FormattableString.Invariant(
                        $"{OpenApiContract.SpecRelativePath} is missing. Generate it with: {OpenApiContract.RegenerateCommand}"),
                    specPath);
            }

            return JsonDocument.Parse(File.ReadAllBytes(specPath));
        }

        /// <summary>
        /// Enumerates every parameter of every operation, including the path-level parameters an
        /// operation inherits.
        /// </summary>
        /// <param name="root">The document root.</param>
        /// <returns>Every parameter site in the document.</returns>
        internal static IReadOnlyList<ParameterSite> EnumerateParameters(JsonElement root)
        {
            var sites = new List<ParameterSite>();
            if (!root.TryGetProperty("paths", out var paths))
            {
                return sites;
            }

            foreach (var pathEntry in paths.EnumerateObject())
            {
                var shared = pathEntry.Value.TryGetProperty("parameters", out var sharedParameters)
                    && sharedParameters.ValueKind == JsonValueKind.Array
                        ? sharedParameters.EnumerateArray().ToArray()
                        : [];

                foreach (var operationKey in _operationKeys)
                {
                    if (!pathEntry.Value.TryGetProperty(operationKey, out var operation))
                    {
                        continue;
                    }

                    var operationId = operation.TryGetProperty("operationId", out var id) ? id.GetString() : null;
                    var own = operation.TryGetProperty("parameters", out var ownParameters)
                        && ownParameters.ValueKind == JsonValueKind.Array
                            ? ownParameters.EnumerateArray()
                            : Enumerable.Empty<JsonElement>();

                    foreach (var parameter in shared.Concat(own))
                    {
                        sites.Add(new ParameterSite(
                            operationKey.ToUpperInvariant(),
                            pathEntry.Name,
                            operationId,
                            parameter));
                    }
                }
            }

            return sites;
        }

        /// <summary>
        /// Enumerates every operation in the document.
        /// </summary>
        /// <param name="root">The document root.</param>
        /// <returns>Method, path and the Operation Object.</returns>
        internal static IReadOnlyList<(string Method, string Path, JsonElement Operation)> EnumerateOperations(JsonElement root)
        {
            var operations = new List<(string, string, JsonElement)>();
            if (!root.TryGetProperty("paths", out var paths))
            {
                return operations;
            }

            foreach (var pathEntry in paths.EnumerateObject())
            {
                foreach (var operationKey in _operationKeys)
                {
                    if (pathEntry.Value.TryGetProperty(operationKey, out var operation))
                    {
                        operations.Add((operationKey.ToUpperInvariant(), pathEntry.Name, operation));
                    }
                }
            }

            return operations;
        }

        /// <summary>
        /// Resolves a schema to the flat set of keywords a consumer sees, following <c>$ref</c> and
        /// flattening <c>allOf</c>.
        /// </summary>
        /// <param name="schema">The schema object, possibly a reference or an <c>allOf</c> wrapper.</param>
        /// <param name="root">The document root, used to dereference.</param>
        /// <returns>The merged keywords.</returns>
        internal static IReadOnlyDictionary<string, JsonElement> ResolveSchema(JsonElement schema, JsonElement root)
        {
            var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            Merge(schema, root, merged, 0);
            return merged;
        }

        /// <summary>
        /// Resolves a parameter's <c>schema</c>. A parameter with no schema at all resolves to an
        /// empty keyword set rather than throwing, so a malformed document surfaces as a failed
        /// assertion in the calling test instead of an exception with no context.
        /// </summary>
        /// <param name="site">The parameter site.</param>
        /// <param name="root">The document root.</param>
        /// <returns>The merged keywords of the parameter's schema.</returns>
        internal static IReadOnlyDictionary<string, JsonElement> ResolveParameterSchema(ParameterSite site, JsonElement root)
            => site.Parameter.TryGetProperty("schema", out var schema)
                ? ResolveSchema(schema, root)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        /// <summary>
        /// Reads the <c>type</c> keyword of a resolved schema as a string, or <see langword="null"/>
        /// when it is absent.
        /// </summary>
        /// <param name="resolved">A resolved keyword set.</param>
        /// <returns>The declared type.</returns>
        internal static string? TypeOf(IReadOnlyDictionary<string, JsonElement> resolved)
            => resolved.TryGetValue("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;

        /// <summary>
        /// Reads the <c>format</c> keyword of a resolved schema as a string, or
        /// <see langword="null"/> when it is absent.
        /// </summary>
        /// <param name="resolved">A resolved keyword set.</param>
        /// <returns>The declared format.</returns>
        internal static string? FormatOf(IReadOnlyDictionary<string, JsonElement> resolved)
            => resolved.TryGetValue("format", out var format) && format.ValueKind == JsonValueKind.String
                ? format.GetString()
                : null;

        private static void Merge(
            JsonElement schema,
            JsonElement root,
            Dictionary<string, JsonElement> merged,
            int depth)
        {
            // A malformed or self-referential document must not hang the suite. The deepest chain in
            // this contract is two links; 32 is far beyond anything legitimate.
            if (depth > 32)
            {
                throw new InvalidOperationException(
                    "Schema resolution exceeded 32 levels; the document contains a $ref or allOf cycle.");
            }

            if (schema.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // OpenAPI 3.0: siblings of $ref are ignored, so the reference replaces this object.
            if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
            {
                Merge(Dereference(reference.GetString()!, root), root, merged, depth + 1);
                return;
            }

            if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
            {
                foreach (var member in allOf.EnumerateArray())
                {
                    Merge(member, root, merged, depth + 1);
                }
            }

            // The containing schema's own keywords win over anything it extends.
            foreach (var property in schema.EnumerateObject())
            {
                if (string.Equals(property.Name, "allOf", StringComparison.Ordinal)
                    || string.Equals(property.Name, "$ref", StringComparison.Ordinal))
                {
                    continue;
                }

                merged[property.Name] = property.Value;
            }
        }

        private static JsonElement Dereference(string reference, JsonElement root)
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"Only local references are expected in this contract, got '{reference}'."));
            }

            var current = root;
            foreach (var rawToken in reference[2..].Split('/'))
            {
                // RFC 6901 escaping, in the order the specification mandates.
                var token = rawToken.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (!current.TryGetProperty(token, out current))
                {
                    throw new InvalidOperationException(
                        string.Create(CultureInfo.InvariantCulture, $"Dangling reference '{reference}': '{token}' does not exist."));
                }
            }

            return current;
        }

        /// <summary>
        /// One parameter, together with the operation it belongs to.
        /// </summary>
        /// <param name="Method">The upper-case HTTP method.</param>
        /// <param name="Path">The templated path.</param>
        /// <param name="OperationId">The operation id, or <see langword="null"/> if absent.</param>
        /// <param name="Parameter">The Parameter Object itself.</param>
        internal sealed record ParameterSite(string Method, string Path, string? OperationId, JsonElement Parameter)
        {
            /// <summary>
            /// Gets the parameter name.
            /// </summary>
            public string Name => Parameter.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;

            /// <summary>
            /// Gets the parameter location (<c>path</c>, <c>query</c>, <c>header</c>, <c>cookie</c>).
            /// </summary>
            public string In => Parameter.TryGetProperty("in", out var location) ? location.GetString() ?? string.Empty : string.Empty;

            /// <summary>
            /// Gets the declared <c>style</c>, or <see langword="null"/> when the parameter relies on
            /// the location default.
            /// </summary>
            public string? Style => Parameter.TryGetProperty("style", out var style) ? style.GetString() : null;

            /// <summary>
            /// Gets the declared <c>explode</c>, or <see langword="null"/> when it is absent. Absent
            /// is deliberately distinguished from <see langword="false"/>: for <c>deepObject</c> the
            /// two are the same undefined combination, and the point of #226 is that neither is
            /// acceptable.
            /// </summary>
            public bool? Explode => Parameter.TryGetProperty("explode", out var explode)
                ? explode.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                }
                : null;

            /// <summary>
            /// Gets a human-readable identity for assertion messages.
            /// </summary>
            public string Describe => string.Create(
                CultureInfo.InvariantCulture,
                $"{Method} {Path} parameter '{Name}' (in: {In}, operationId: {OperationId ?? "<none>"})");
        }
    }
}
