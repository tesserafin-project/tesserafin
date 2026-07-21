#pragma warning disable CS1591

using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Providers;

namespace Tesserafin.Controller.Library;

public interface IItemNamingService
{
    int? GetSeasonNumberFromPath(string path, string? parentPath);

    bool FillMissingEpisodeNumbersFromPath(Episode episode, bool forceRefresh);

    ItemLookupInfo ParseName(string name);
}
