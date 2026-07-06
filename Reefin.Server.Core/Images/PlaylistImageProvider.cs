#nullable disable

#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using Reefin.Common.Configuration;
using Reefin.Controller.Drawing;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Entities.TV;
using Reefin.Controller.Playlists;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.IO;

namespace Reefin.Server.Core.Images
{
    public class PlaylistImageProvider : BaseDynamicImageProvider<Playlist>
    {
        public PlaylistImageProvider(IFileSystem fileSystem, IProviderManager providerManager, IApplicationPaths applicationPaths, IImageProcessor imageProcessor) : base(fileSystem, providerManager, applicationPaths, imageProcessor)
        {
        }

        protected override IReadOnlyList<BaseItem> GetItemsWithImages(BaseItem item)
        {
            var playlist = (Playlist)item;

            return playlist.GetManageableItems()
                .Select(i =>
                {
                    var subItem = i.Item2;

                    if (subItem is Episode episode)
                    {
                        var series = episode.Series;
                        if (series is not null && series.HasImage(ImageType.Primary))
                        {
                            return series;
                        }
                    }

                    if (subItem.HasImage(ImageType.Primary))
                    {
                        return subItem;
                    }

                    var parent = subItem.GetOwner() ?? subItem.GetParent();

                    if (parent is not null && parent.HasImage(ImageType.Primary))
                    {
                        if (parent is MusicAlbum)
                        {
                            return parent;
                        }
                    }

                    return null;
                })
                .Where(i => i is not null)
                .DistinctBy(x => x.Id)
                .ToList();
        }
    }
}
