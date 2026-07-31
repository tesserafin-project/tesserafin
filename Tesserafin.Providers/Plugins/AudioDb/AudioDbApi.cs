using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Tesserafin.Providers.Plugins.AudioDb
{
    /// <summary>
    /// Resolves the operator-supplied TheAudioDB credential into a request base URL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tesserafin ships no TheAudioDB credential. The key that used to be compiled in here belonged
    /// to another project's account, so it was removed rather than annotated; TheAudioDB is now
    /// operator-configured only, through <see cref="PluginConfiguration.AudioDbApiKey"/>.
    /// </para>
    /// <para>
    /// TheAudioDB carries its credential as a URL <em>path segment</em> rather than a query
    /// parameter, so there is no request that can be issued without one. Every caller therefore has
    /// to ask this type first and do nothing when the answer is <see langword="false"/> — the
    /// unconfigured state is a normal state, not an error, and must not throw or reach the network.
    /// </para>
    /// <para>
    /// Four provider classes share this seam (artist, album and their two image providers), and each
    /// is instantiated once at startup. A per-provider warning would therefore mean four identical
    /// log lines for one missing setting, so the diagnostic is latched here and emitted once for the
    /// whole plugin.
    /// </para>
    /// </remarks>
    public static class AudioDbApi
    {
        /// <summary>
        /// The anonymous root of TheAudioDB JSON API. This carries no credential: an authenticated
        /// request is this root followed by the operator's key as the next path segment.
        /// </summary>
        public const string ApiRoot = "https://www.theaudiodb.com/api/v1/json";

        private static int _unconfiguredWarningEmitted;

        /// <summary>
        /// Gets a value indicating whether an operator has supplied a TheAudioDB API key.
        /// </summary>
        /// <remarks>
        /// Blank and whitespace-only values count as missing: TheAudioDB would answer such a request
        /// with an error page rather than JSON, so treating them as configured would trade a clear
        /// "not configured" diagnostic for an opaque deserialisation failure.
        /// </remarks>
        public static bool IsConfigured
            => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.AudioDbApiKey);

        /// <summary>
        /// Builds the credential-bearing base URL for TheAudioDB from the operator's configuration.
        /// </summary>
        /// <param name="logger">Logger used to emit the one-time unconfigured diagnostic.</param>
        /// <param name="baseUrl">
        /// When this method returns <see langword="true"/>, the base URL an endpoint path is appended
        /// to; otherwise <see langword="null"/>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when an operator-supplied key exists and a request may be issued;
        /// otherwise <see langword="false"/>, in which case the caller must issue no request.
        /// </returns>
        public static bool TryGetBaseUrl(ILogger logger, [NotNullWhen(true)] out string? baseUrl)
        {
            ArgumentNullException.ThrowIfNull(logger);

            var apiKey = Plugin.Instance?.Configuration.AudioDbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                WarnUnconfiguredOnce(logger);
                baseUrl = null;
                return false;
            }

            baseUrl = ApiRoot + "/" + Uri.EscapeDataString(apiKey.Trim());
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
                "No TheAudioDB API key is configured. TheAudioDB artist and album metadata and images are unavailable until an API key is set on the AudioDB plugin configuration page and the server is restarted. Other metadata providers are unaffected.");
        }
    }
}
