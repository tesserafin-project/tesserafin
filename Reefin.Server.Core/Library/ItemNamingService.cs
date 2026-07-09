#pragma warning disable CS1591

using System;
using System.IO;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.TV;
using Reefin.Controller.Library;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Naming.Common;
using Reefin.Naming.TV;
using NamingEpisodeInfo = Reefin.Naming.TV.EpisodeInfo;
using NamingEpisodeResolver = Reefin.Naming.TV.EpisodeResolver;
using VideoResolver = Reefin.Naming.Video.VideoResolver;

namespace Reefin.Server.Core.Library;

public class ItemNamingService : IItemNamingService
{
    private readonly NamingOptions _namingOptions;

    public ItemNamingService(NamingOptions namingOptions)
    {
        _namingOptions = namingOptions;
    }

    public int? GetSeasonNumberFromPath(string path, string? parentPath)
    {
        return SeasonPathParser.Parse(path, parentPath, true, true).SeasonNumber;
    }

    public bool FillMissingEpisodeNumbersFromPath(Episode episode, bool forceRefresh)
    {
        var series = episode.Series;
        bool? isAbsoluteNaming = series is not null && string.Equals(series.DisplayOrder, "absolute", StringComparison.OrdinalIgnoreCase);
        if (!isAbsoluteNaming.Value)
        {
            // In other words, no filter applied
            isAbsoluteNaming = null;
        }

        var resolver = new NamingEpisodeResolver(_namingOptions);

        var isFolder = episode.VideoType == VideoType.BluRay || episode.VideoType == VideoType.Dvd;

        NamingEpisodeInfo? episodeInfo = null;
        if (episode.IsFileProtocol)
        {
            episodeInfo = resolver.Resolve(episode.Path, isFolder, null, null, isAbsoluteNaming);
            // Resolve from parent folder if it's not the Season folder
            var parent = episode.GetParent();
            if (episodeInfo is null && parent.GetType() == typeof(Folder))
            {
                episodeInfo = resolver.Resolve(parent.Path, true, null, null, isAbsoluteNaming);
                if (episodeInfo is not null)
                {
                    // add the container
                    episodeInfo.Container = Path.GetExtension(episode.Path)?.TrimStart('.');
                }
            }
        }

        var changed = false;
        if (episodeInfo is null)
        {
            return changed;
        }

        if (episodeInfo.IsByDate)
        {
            if (episode.IndexNumber.HasValue)
            {
                episode.IndexNumber = null;
                changed = true;
            }

            if (episode.IndexNumberEnd.HasValue)
            {
                episode.IndexNumberEnd = null;
                changed = true;
            }

            if (!episode.PremiereDate.HasValue)
            {
                if (episodeInfo.Year.HasValue && episodeInfo.Month.HasValue && episodeInfo.Day.HasValue)
                {
                    episode.PremiereDate = new DateTime(episodeInfo.Year.Value, episodeInfo.Month.Value, episodeInfo.Day.Value).ToUniversalTime();
                }

                if (episode.PremiereDate.HasValue)
                {
                    changed = true;
                }
            }

            if (!episode.ProductionYear.HasValue)
            {
                episode.ProductionYear = episodeInfo.Year;

                if (episode.ProductionYear.HasValue)
                {
                    changed = true;
                }
            }
        }
        else
        {
            if (!episode.IndexNumber.HasValue || forceRefresh)
            {
                if (episode.IndexNumber != episodeInfo.EpisodeNumber)
                {
                    changed = true;
                }

                episode.IndexNumber = episodeInfo.EpisodeNumber;
            }

            if (!episode.IndexNumberEnd.HasValue || forceRefresh)
            {
                if (episode.IndexNumberEnd != episodeInfo.EndingEpisodeNumber)
                {
                    changed = true;
                }

                episode.IndexNumberEnd = episodeInfo.EndingEpisodeNumber;
            }

            if (!episode.ParentIndexNumber.HasValue || forceRefresh)
            {
                if (episode.ParentIndexNumber != episodeInfo.SeasonNumber)
                {
                    changed = true;
                }

                episode.ParentIndexNumber = episodeInfo.SeasonNumber;
            }
        }

        if (!episode.ParentIndexNumber.HasValue)
        {
            var season = episode.Season;

            if (season is not null)
            {
                episode.ParentIndexNumber = season.IndexNumber;
            }

            if (episode.ParentIndexNumber.HasValue)
            {
                changed = true;
            }
        }

        return changed;
    }

    public ItemLookupInfo ParseName(string name)
    {
        var result = VideoResolver.CleanDateTime(name, _namingOptions);

        return new ItemLookupInfo
        {
            Name = VideoResolver.TryCleanString(result.Name, _namingOptions, out var newName) ? newName : result.Name,
            Year = result.Year
        };
    }
}
