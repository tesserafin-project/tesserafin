#pragma warning disable CS1591

using Tesserafin.Model.Plugins;

namespace Tesserafin.Providers.Plugins.Omdb
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the API key used to access OMDb. Tesserafin ships no built-in key, so this is
        /// the only source of one: the OMDb providers stay inert until an operator supplies their own
        /// on the settings page.
        /// </summary>
        public string OmdbApiKey { get; set; } = string.Empty;

        public bool CastAndCrew { get; set; }
    }
}
