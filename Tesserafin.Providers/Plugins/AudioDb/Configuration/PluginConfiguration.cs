#pragma warning disable CS1591

using Tesserafin.Model.Plugins;

namespace Tesserafin.Providers.Plugins.AudioDb
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the API key used to access TheAudioDB. Tesserafin ships no built-in key, so
        /// this is the only source of one: the TheAudioDB providers stay inert until an operator
        /// supplies their own on the settings page.
        /// </summary>
        public string AudioDbApiKey { get; set; } = string.Empty;

        public bool ReplaceAlbumName { get; set; }
    }
}
