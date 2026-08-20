using System;
using System.Collections.Generic;
using System.Text;

namespace Tesserafin.Api.Helpers;

/// <summary>
/// Adds a validated playback capability to the media uris of an HLS manifest, at the moment the
/// manifest is served (#153-LTV-S1).
/// </summary>
/// <remarks>
/// WHY A PURE TRANSFORMER OVER THE RESPONSE. The Live TV playlist on disk is written by ffmpeg.
/// Anything that put a credential into the segment uris earlier than this — a different
/// <c>-hls_base_url</c>, a post-processing pass over the file — would write the secret to disk,
/// hand it to ffmpeg's argv, or both, and #153-LTV-S0's interdiction on the server→ffmpeg path is
/// absolute. Transforming the response text and nothing else makes four contract clauses true by
/// construction rather than by assertion: the `.m3u8` on disk stays credential-free, ffmpeg's
/// command line, environment and logs never see the value, and no non-live path is touched.
///
/// WHAT IT REFUSES. Every uri form this Live TV path was measured to emit is transformed: segment
/// lines and <c>#EXT-X-MAP:URI</c>. Every OTHER tag carrying a quoted <c>URI="…"</c> is refused
/// outright — including ones with well-known names. <c>#EXT-X-KEY</c> and
/// <c>#EXT-X-SESSION-KEY</c> would point at a key server, which must never receive a media
/// capability; <c>#EXT-X-PART</c>, <c>#EXT-X-PRELOAD-HINT</c> and <c>#EXT-X-RENDITION-REPORT</c>
/// are low-latency forms this muxer configuration does not produce. Silently passing any of them
/// through would emit a uri the client cannot fetch, which looks like a playback bug rather than an
/// unhandled manifest shape. Fail-closed is deliberate: a manifest this code does not fully
/// understand must not be served with a credential in it.
/// </remarks>
public static class HlsManifestCredentialPropagator
{
    /// <summary>
    /// The query key a capability travels in. Kept in step with
    /// <c>PlaybackCapabilityAuthenticationHandler.QueryKey</c> by
    /// <c>HlsManifestCredentialPropagatorTests.CapabilityKey_IsTheKeyAuthenticationReads</c>.
    /// </summary>
    public const string CapabilityKey = "playbackCapability";

    /// <summary>
    /// The query key naming the media source. Not a credential: it is an identifier the modern HLS
    /// routes already carry in every url, and the legacy segment route needs it to know which media
    /// source the presented capability has to agree with.
    /// </summary>
    public const string MediaSourceKey = "mediaSourceId";

    /// <summary>
    /// The play-session parameter every emitted uri carries when the capability has one
    /// (#153-LTV-R1). The legacy segment route demands it, so a fragment uri that omitted it would
    /// be refused — which is the point: LTV-R0 reached a segment with a capability minted under a
    /// play session the server had never issued, because nothing on that route ever asked.
    /// </summary>
    public const string PlaySessionKey = "playSessionId";

    /// <summary>
    /// The one tag whose <c>URI="…"</c> this path emits and this transformer rewrites.
    /// </summary>
    private const string MapTag = "#EXT-X-MAP:";

    /// <summary>
    /// Names that must never be added to a uri by this transformer. The durable session token
    /// travels under the first two, the header form under the third, and the socket ticket under
    /// the fourth; none of them is short-lived, and a manifest is the worst place to put one.
    /// </summary>
    private static readonly string[] _forbiddenParameterNames =
    {
        "ApiKey",
        "api_key",
        "Authorization",
        "webSocketTicket"
    };

    /// <summary>
    /// Returns <paramref name="manifest"/> with the capability added to every media uri it names.
    /// </summary>
    /// <param name="manifest">The manifest text as served.</param>
    /// <param name="capabilityValue">The validated capability value.</param>
    /// <param name="mediaSourceId">The media source the capability is bound to, or null.</param>
    /// <param name="origin">The origin the request arrived on, used to tell an external uri apart from a same-origin one.</param>
    /// <param name="playSessionId">The play session the validated capability belongs to, or null when it has none.</param>
    /// <returns>The transformed manifest.</returns>
    /// <exception cref="InvalidOperationException">A uri already carries a different or duplicated capability, or the manifest names a uri form this transformer does not handle.</exception>
    public static string Propagate(string manifest, string capabilityValue, string? mediaSourceId, Uri origin, string? playSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(capabilityValue);
        ArgumentNullException.ThrowIfNull(origin);

        var parameters = new List<KeyValuePair<string, string>>(3)
        {
            new(CapabilityKey, capabilityValue)
        };

        if (!string.IsNullOrEmpty(mediaSourceId))
        {
            parameters.Add(new(MediaSourceKey, mediaSourceId));
        }

        // #153-LTV-R1. Omitted only when the capability genuinely has no play session, which is
        // the one case where the route's demand is null too and the two agree.
        if (!string.IsNullOrEmpty(playSessionId))
        {
            parameters.Add(new(PlaySessionKey, playSessionId));
        }

        foreach (var parameter in parameters)
        {
            foreach (var forbidden in _forbiddenParameterNames)
            {
                if (string.Equals(parameter.Key, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"'{forbidden}' must never be propagated into an HLS manifest uri.");
                }
            }
        }

        // Line endings are preserved exactly: the manifest is split on the separators themselves,
        // so a CRLF file stays CRLF and a mixed one stays mixed. Rebuilding it with Environment
        // .NewLine would rewrite every untouched line, which a byte comparison would then call a
        // change this transformer made.
        var builder = new StringBuilder(manifest.Length + 128);
        var index = 0;
        while (index < manifest.Length)
        {
            var lineEnd = manifest.IndexOf('\n', index);
            string line;
            string separator;
            if (lineEnd < 0)
            {
                line = manifest[index..];
                separator = string.Empty;
                index = manifest.Length;
            }
            else
            {
                var contentEnd = lineEnd > index && manifest[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
                line = manifest[index..contentEnd];
                separator = manifest[contentEnd..(lineEnd + 1)];
                index = lineEnd + 1;
            }

            builder.Append(Transform(line, parameters, origin)).Append(separator);
        }

        return builder.ToString();
    }

    private static string Transform(string line, List<KeyValuePair<string, string>> parameters, Uri origin)
    {
        // A blank line and a plain comment are content this transformer has no business touching.
        if (line.Length == 0 || line.AsSpan().TrimEnd().Length == 0)
        {
            return line;
        }

        if (line[0] != '#')
        {
            return AppendParameters(line, parameters, origin);
        }

        if (line.StartsWith(MapTag, StringComparison.Ordinal))
        {
            return RewriteQuotedUri(line, parameters, origin);
        }

        if (line.Contains("URI=\"", StringComparison.Ordinal))
        {
            var tagEnd = line.IndexOf(':', StringComparison.Ordinal);
            var tag = tagEnd < 0 ? line : line[..tagEnd];
            throw new InvalidOperationException(
                $"'{tag}' carries a uri this manifest transformer does not classify. "
                + "Refusing rather than serving a manifest with an unhandled uri form in it.");
        }

        return line;
    }

    private static string RewriteQuotedUri(string line, List<KeyValuePair<string, string>> parameters, Uri origin)
    {
        const string Marker = "URI=\"";
        var start = line.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return line;
        }

        var uriStart = start + Marker.Length;
        var uriEnd = line.IndexOf('"', uriStart);
        if (uriEnd < 0)
        {
            throw new InvalidOperationException("An attribute list names an unterminated URI.");
        }

        var uri = line[uriStart..uriEnd];
        return string.Concat(line.AsSpan(0, uriStart), AppendParameters(uri, parameters, origin), line.AsSpan(uriEnd));
    }

    private static string AppendParameters(string uri, List<KeyValuePair<string, string>> parameters, Uri origin)
    {
        if (uri.Length == 0 || IsExternal(uri, origin))
        {
            // Classified and left alone. A capability is bound to this server's session; handing it
            // to another origin would be a credential disclosure with no upside.
            return uri;
        }

        var hashIndex = uri.IndexOf('#', StringComparison.Ordinal);
        var fragment = hashIndex >= 0 ? uri[hashIndex..] : string.Empty;
        var head = hashIndex >= 0 ? uri[..hashIndex] : uri;

        var questionIndex = head.IndexOf('?', StringComparison.Ordinal);
        var path = questionIndex >= 0 ? head[..questionIndex] : head;
        var query = questionIndex >= 0 ? head[(questionIndex + 1)..] : string.Empty;

        // The existing query is carried across as its own bytes, never re-serialized. Parsing it
        // into pairs and writing it back would re-encode every value, and "encoded exactly once"
        // is a contract clause, not a nicety.
        var builder = new StringBuilder(uri.Length + 96);
        builder.Append(path);
        var wroteAny = query.Length > 0;
        builder.Append('?').Append(query);

        foreach (var parameter in parameters)
        {
            var existing = FindExistingValue(query, parameter.Key);
            if (existing.Count > 1)
            {
                throw new InvalidOperationException(
                    $"A manifest uri already carries '{parameter.Key}' more than once.");
            }

            if (existing.Count == 1)
            {
                if (!string.Equals(existing.Value, parameter.Value, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"A manifest uri already carries a different '{parameter.Key}'.");
                }

                // Already exactly this value, exactly once. Adding it again would put two of them
                // in one uri, which the contract forbids outright.
                continue;
            }

            if (wroteAny)
            {
                builder.Append('&');
            }

            builder
                .Append(parameter.Key)
                .Append('=')
                .Append(Uri.EscapeDataString(parameter.Value));
            wroteAny = true;
        }

        builder.Append(fragment);
        return builder.ToString();
    }

    /// <summary>
    /// How many times <paramref name="key"/> appears in <paramref name="query"/>, and its value if
    /// it appears exactly once.
    /// </summary>
    private static (int Count, string? Value) FindExistingValue(string query, string key)
    {
        if (query.Length == 0)
        {
            return (0, null);
        }

        var count = 0;
        string? value = null;
        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0)
            {
                continue;
            }

            var equalsIndex = pair.IndexOf('=', StringComparison.Ordinal);
            var name = equalsIndex < 0 ? pair : pair[..equalsIndex];

            // Query keys are matched exactly, because that is how ASP.NET Core reads them back:
            // `Request.Query["playbackCapability"]` does not see `PlaybackCapability`. Matching
            // case-insensitively here would make this transformer skip a key the server will not
            // read, and the uri would go out with no capability the route can find.
            if (!string.Equals(name, key, StringComparison.Ordinal))
            {
                continue;
            }

            count++;
            value = equalsIndex < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equalsIndex + 1)..]);
        }

        return (count, count == 1 ? value : null);
    }

    private static bool IsExternal(string uri, Uri origin)
    {
        // Protocol-relative, e.g. //cdn.example/seg.ts — it inherits the scheme but names its own
        // authority, so it is external unless that authority is this one.
        if (uri.StartsWith("//", StringComparison.Ordinal))
        {
            return !Uri.TryCreate(origin.Scheme + ":" + uri, UriKind.Absolute, out var inherited)
                   || !IsSameOrigin(inherited, origin);
        }

        // The scheme is detected by hand rather than by asking Uri to parse. On Unix,
        // `Uri.TryCreate("/videos/x/seg.ts", UriKind.Absolute, …)` SUCCEEDS with scheme `file`,
        // so a root-relative segment uri would be read as an absolute one on a foreign origin and
        // silently left uncredentialed. Measured: it made the root-relative case emit the uri
        // unchanged.
        if (!HasScheme(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var absolute))
        {
            // Relative, including root-relative: this server serves it.
            return false;
        }

        return !IsSameOrigin(absolute, origin);
    }

    /// <summary>
    /// Whether the uri begins with an RFC 3986 scheme followed by a colon.
    /// </summary>
    private static bool HasScheme(string uri)
    {
        var colon = uri.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        if (!char.IsAsciiLetter(uri[0]))
        {
            return false;
        }

        for (var i = 1; i < colon; i++)
        {
            var c = uri[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSameOrigin(Uri candidate, Uri origin)
        => string.Equals(candidate.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(candidate.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
           && candidate.Port == origin.Port;
}
