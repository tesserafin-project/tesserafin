using Tesserafin.Controller.Entities;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// The rating score and the server's country code that the parental check reads, on top of
    /// <see cref="IStandaloneVisibilityService"/>.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="BaseItem"/> (DEBT-SERVER-8): the parental check scores the rating through the
    /// static <c>BaseItem.LocalizationManager</c> and falls back to the country code of the static
    /// <c>BaseItem.ConfigurationManager</c>. Passed as the <see cref="ITagVisibilityService"/> of its visibility
    /// check, it supplies both instead, while the rules stay the entities' own. Implementations must not depend
    /// on <see cref="ILibraryManager"/>, directly or deferred.
    /// </remarks>
    public interface IRatingVisibilityService : IStandaloneVisibilityService
    {
        /// <summary>
        /// Gets the score of a rating in a country, as <see cref="Tesserafin.Model.Globalization.ILocalizationManager.GetRatingScore"/> does.
        /// </summary>
        /// <param name="rating">The rating.</param>
        /// <param name="countryCode">The country code; <c>null</c> for the server's.</param>
        /// <returns>The score; <c>null</c> when the rating is not recognised.</returns>
        ParentalRatingScore? GetRatingScore(string rating, string? countryCode);

        /// <summary>
        /// Gets the server's metadata country code, the last fallback of an item's.
        /// </summary>
        /// <returns>The country code.</returns>
        string GetServerMetadataCountryCode();
    }
}
