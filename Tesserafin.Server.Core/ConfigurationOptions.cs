using System.Collections.Generic;
using static Tesserafin.Controller.Extensions.ConfigurationExtensions;

namespace Tesserafin.Server.Core
{
    /// <summary>
    /// Static class containing the default configuration options for the web server.
    /// </summary>
    public static class ConfigurationOptions
    {
        /// <summary>
        /// Gets a new copy of the default configuration options.
        /// </summary>
        public static Dictionary<string, string?> DefaultConfiguration => new()
        {
            { HostWebClientKey, bool.TrueString },
            { DefaultRedirectKey, "web/" },
            { FfmpegProbeSizeKey, "1G" },
            { FfmpegAnalyzeDurationKey, "200M" },
            { BindToUnixSocketKey, bool.FalseString },
            { SqliteCacheSizeKey, "20000" },
            { FfmpegSkipValidationKey, bool.FalseString },
            { FfmpegImgExtractPerfTradeoffKey, bool.FalseString },
            { DetectNetworkChangeKey, bool.TrueString },

            // Secure bootstrap mode is OFF by default and nothing in the product turns it on: no
            // installer, package, container image or wizard step sets it. An existing installation
            // therefore keeps exactly the bind derivation it has today. Activation is an explicit
            // operator act (tesserafin-project/tesserafin#242).
            { SecureBootstrapKey, bool.FalseString }
        };
    }
}
