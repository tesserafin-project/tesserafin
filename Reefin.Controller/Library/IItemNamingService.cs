#pragma warning disable CS1591

using Reefin.Controller.Entities.TV;
using Reefin.Controller.Providers;

namespace Reefin.Controller.Library;

public interface IItemNamingService
{
    int? GetSeasonNumberFromPath(string path, string? parentPath);

    bool FillMissingEpisodeNumbersFromPath(Episode episode, bool forceRefresh);

    ItemLookupInfo ParseName(string name);
}
