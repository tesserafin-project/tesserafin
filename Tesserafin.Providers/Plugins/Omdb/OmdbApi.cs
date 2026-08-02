using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Tesserafin.Providers.Plugins.Omdb
{
    /// <summary>
    /// Resolves the operator-supplied OMDb credential into a request URL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tesserafin ships no OMDb credential. The value that used to be compiled in here was an
    /// inherited upstream key embedded directly in the request URL's <c>apikey</c> query parameter,
    /// so it was removed rather than annotated; OMDb is now operator-configured only, through
    /// <see cref="PluginConfiguration.OmdbApiKey"/>.
    /// </para>
    /// <para>
    /// OMDb rejects an unauthenticated request, so there is no anonymous form of any call here.
    /// Callers must ask this type first and issue nothing when the answer is
    /// <see langword="false"/> — the unconfigured state is a normal state, not an error.
    /// </para>
    /// <para>
    /// Three provider classes share this seam (item, episode and image), each holding its own
    /// <see cref="OmdbProvider"/>, and each is instantiated once at startup. The diagnostic is
    /// therefore latched here and emitted once for the whole plugin rather than once per provider.
    /// </para>
    /// </remarks>
    public static class OmdbApi
    {
        /// <summary>
        /// The anonymous root of the OMDb API. This carries no credential: an authenticated request
        /// is this root with the operator's key supplied as the <c>apikey</c> query parameter.
        /// </summary>
        public const string ApiRoot = "https://www.omdbapi.com";

        private static int _unconfiguredWarningEmitted;

        /// <summary>
        /// Gets a value indicating whether an operator has supplied an OMDb API key.
        /// </summary>
        /// <remarks>
        /// Blank and whitespace-only values count as missing: OMDb answers such a request with an
        /// error document rather than the expected payload, so treating them as configured would
        /// trade a clear "not configured" diagnostic for an opaque deserialisation failure.
        /// </remarks>
        public static bool IsConfigured
            => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.OmdbApiKey);

        /// <summary>
        /// Builds a credential-bearing OMDb request URL from the operator's configuration.
        /// </summary>
        /// <param name="query">Query string appended to the request, without a leading separator.</param>
        /// <param name="logger">Logger used to emit the one-time unconfigured diagnostic.</param>
        /// <param name="url">
        /// When this method returns <see langword="true"/>, the request URL; otherwise
        /// <see langword="null"/>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when an operator-supplied key exists and a request may be issued;
        /// otherwise <see langword="false"/>, in which case the caller must issue no request.
        /// </returns>
        public static bool TryGetRequestUrl(string? query, ILogger logger, [NotNullWhen(true)] out string? url)
        {
            ArgumentNullException.ThrowIfNull(logger);

            var apiKey = Plugin.Instance?.Configuration.OmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                WarnUnconfiguredOnce(logger);
                url = null;
                return false;
            }

            url = ApiRoot + "?apikey=" + Uri.EscapeDataString(apiKey.Trim());

            if (!string.IsNullOrWhiteSpace(query))
            {
                url += "&" + query;
            }

            return true;
        }

        /// <summary>
        /// Clears the one-time unconfigured-warning latch.
        /// </summary>
        /// <remarks>
        /// Exists so tests can assert the latch's behaviour more than once in a process. Production
        /// code never calls it: the warning is meant to be emitted once per server lifetime.
        /// </remarks>
        public static void ResetUnconfiguredWarningLatch()
            => Interlocked.Exchange(ref _unconfiguredWarningEmitted, 0);

        private static void WarnUnconfiguredOnce(ILogger logger)
        {
            if (Interlocked.Exchange(ref _unconfiguredWarningEmitted, 1) != 0)
            {
                return;
            }

            logger.LogWarning(
                "No OMDb API key is configured. OMDb metadata and images are unavailable until an API key is set on the OMDb plugin configuration page and the server is restarted. Other metadata providers are unaffected.");
        }
    }
}
