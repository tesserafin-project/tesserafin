#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reefin.Common.Configuration;
using Reefin.Controller.Channels;
using Reefin.Controller.Collections;
using Reefin.Controller.Drawing;
using Reefin.Controller.Dto;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Entities.TV;
using Reefin.Controller.Library;
using Reefin.Controller.Providers;
using Reefin.Controller.TV;
using Reefin.Data.Enums;
using Reefin.Extensions;
using Reefin.Model.Entities;
using Reefin.Model.IO;

namespace Reefin.Server.Core.Images
{
    public class DynamicImageProvider : BaseDynamicImageProvider<UserView>
    {
        private readonly IUserManager _userManager;
        private readonly IChannelManager _channelManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserViewManager _userViewManager;
        private readonly ITVSeriesManager _tvSeriesManager;

        public DynamicImageProvider(IFileSystem fileSystem, IProviderManager providerManager, IApplicationPaths applicationPaths, IImageProcessor imageProcessor, IUserManager userManager, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
            : base(fileSystem, providerManager, applicationPaths, imageProcessor)
        {
            _userManager = userManager;
            _channelManager = channelManager;
            _collectionManager = collectionManager;
            _userViewManager = userViewManager;
            _tvSeriesManager = tvSeriesManager;
        }

        protected override IReadOnlyList<BaseItem> GetItemsWithImages(BaseItem item)
        {
            var view = (UserView)item;

            var isUsingCollectionStrip = IsUsingCollectionStrip(view);
            var recursive = isUsingCollectionStrip && view?.ViewType is not null && view.ViewType != CollectionType.boxsets && view.ViewType != CollectionType.playlists;

            var result = view.GetItemList(
                new InternalItemsQuery
                {
                    User = view.UserId.HasValue ? _userManager.GetUserById(view.UserId.Value) : null,
                    CollapseBoxSetItems = false,
                    Recursive = recursive,
                    ExcludeItemTypes = new[] { BaseItemKind.UserView, BaseItemKind.CollectionFolder, BaseItemKind.Person },
                    DtoOptions = new DtoOptions(false)
                },
                _channelManager,
                _collectionManager,
                _userViewManager,
                _tvSeriesManager);

            var items = result.Select(i =>
            {
                if (i is Episode episode)
                {
                    var series = episode.Series;
                    if (series is not null)
                    {
                        return series;
                    }

                    return episode;
                }

                if (i is Season season)
                {
                    var series = season.Series;
                    if (series is not null)
                    {
                        return series;
                    }

                    return season;
                }

                if (i is Audio audio)
                {
                    var album = audio.AlbumEntity;
                    if (album is not null && album.HasImage(ImageType.Primary))
                    {
                        return album;
                    }
                }

                return i;
            }).DistinctBy(x => x.Id);

            List<BaseItem> returnItems;
            if (isUsingCollectionStrip)
            {
                returnItems = items
                    .Where(i => i.HasImage(ImageType.Primary) || i.HasImage(ImageType.Thumb))
                    .ToList();
                returnItems.Shuffle();
                return returnItems;
            }

            returnItems = items
                .Where(i => i.HasImage(ImageType.Primary))
                .ToList();
            returnItems.Shuffle();
            return returnItems;
        }

        protected override bool Supports(BaseItem item)
        {
            if (item is UserView view)
            {
                return IsUsingCollectionStrip(view);
            }

            return false;
        }

        private static bool IsUsingCollectionStrip(UserView view)
        {
            CollectionType[] collectionStripViewTypes =
            {
                CollectionType.movies,
                CollectionType.tvshows,
                CollectionType.playlists
            };

            return view?.ViewType is not null && collectionStripViewTypes.Contains(view.ViewType.Value);
        }

        protected override string CreateImage(BaseItem item, IReadOnlyCollection<BaseItem> itemsWithImages, string outputPathWithoutExtension, ImageType imageType, int imageIndex)
        {
            if (itemsWithImages.Count == 0)
            {
                return null;
            }

            var outputPath = Path.ChangeExtension(outputPathWithoutExtension, ".png");

            return CreateThumbCollage(item, itemsWithImages, outputPath, 960, 540);
        }
    }
}
