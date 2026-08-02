using Tesserafin.Model.Plugins;

namespace Tesserafin.Providers.Plugins.Tmdb
{
    /// <summary>
    /// Plugin configuration class for TMDb library.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the API key used to access TMDb. Tesserafin ships no built-in key, so this is the only
        /// source of one: the TMDb provider stays inert until an operator supplies their own on the settings page.
        /// </summary>
        public string TmdbApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether include adult content when searching with TMDb.
        /// </summary>
        public bool IncludeAdult { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether tags should be imported for series from TMDb.
        /// </summary>
        public bool ExcludeTagsSeries { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether tags should be imported for movies from TMDb.
        /// </summary>
        public bool ExcludeTagsMovies { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether season name should be imported from TMDb.
        /// </summary>
        public bool ImportSeasonName { get; set; }

        /// <summary>
        /// Gets or sets a value indicating the maximum number of cast members to fetch for an item.
        /// </summary>
        public int MaxCastMembers { get; set; } = 15;

        /// <summary>
        /// Gets or sets a value indicating the maximum number of crew members to fetch for an item.
        /// </summary>
        public int MaxCrewMembers { get; set; } = 15;

        /// <summary>
        /// Gets or sets a value indicating whether to hide cast members without profile images.
        /// </summary>
        public bool HideMissingCastMembers { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to hide crew members without profile images.
        /// </summary>
        public bool HideMissingCrewMembers { get; set; }

        /// <summary>
        /// Gets or sets a value indicating the poster image size to fetch.
        /// </summary>
        public string? PosterSize { get; set; }

        /// <summary>
        /// Gets or sets a value indicating the backdrop image size to fetch.
        /// </summary>
        public string? BackdropSize { get; set; }

        /// <summary>
        /// Gets or sets a value indicating the logo image size to fetch.
        /// </summary>
        public string? LogoSize { get; set; }

        /// <summary>
        /// Gets or sets a value indicating the profile image size to fetch.
        /// </summary>
        public string? ProfileSize { get; set; }

        /// <summary>
        /// Gets or sets a value indicating the still image size to fetch.
        /// </summary>
        public string? StillSize { get; set; }

        /// <summary>
        /// Gets or sets the cache duration in days for similar item results. A value of 0 disables caching.
        /// </summary>
        public int SimilarItemsCacheDays { get; set; } = 7;
    }
}
