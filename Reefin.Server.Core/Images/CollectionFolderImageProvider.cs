#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using Reefin.Api.Extensions;
using Reefin.Common.Configuration;
using Reefin.Controller.Channels;
using Reefin.Controller.Collections;
using Reefin.Controller.Drawing;
using Reefin.Controller.Dto;
using Reefin.Controller.Entities;
using Reefin.Controller.Library;
using Reefin.Controller.Providers;
using Reefin.Controller.TV;
using Reefin.Data.Enums;
using Reefin.Database.Implementations.Enums;
using Reefin.Model.Entities;
using Reefin.Model.IO;

namespace Reefin.Server.Core.Images
{
    public class CollectionFolderImageProvider : BaseDynamicImageProvider<CollectionFolder>
    {
        private readonly IChannelManager _channelManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserViewManager _userViewManager;
        private readonly ITVSeriesManager _tvSeriesManager;

        public CollectionFolderImageProvider(IFileSystem fileSystem, IProviderManager providerManager, IApplicationPaths applicationPaths, IImageProcessor imageProcessor, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager) : base(fileSystem, providerManager, applicationPaths, imageProcessor)
        {
            _channelManager = channelManager;
            _collectionManager = collectionManager;
            _userViewManager = userViewManager;
            _tvSeriesManager = tvSeriesManager;
        }

        protected override IReadOnlyList<BaseItem> GetItemsWithImages(BaseItem item)
        {
            var view = (CollectionFolder)item;
            var viewType = view.CollectionType;
            var includeItemTypes = DtoExtensions.GetBaseItemKindsForCollectionType(viewType);
            var recursive = viewType != CollectionType.playlists;

            return view.GetItemList(
                new InternalItemsQuery
                {
                    CollapseBoxSetItems = false,
                    Recursive = recursive,
                    DtoOptions = new DtoOptions(false),
                    ImageTypes = [ImageType.Primary],
                    Limit = 8,
                    OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)],
                    IncludeItemTypes = includeItemTypes
                },
                _channelManager,
                _collectionManager,
                _userViewManager,
                _tvSeriesManager);
        }

        protected override bool Supports(BaseItem item)
        {
            return item is CollectionFolder;
        }

        protected override string CreateImage(BaseItem item, IReadOnlyCollection<BaseItem> itemsWithImages, string outputPathWithoutExtension, ImageType imageType, int imageIndex)
        {
            var outputPath = Path.ChangeExtension(outputPathWithoutExtension, ".png");

            if (imageType == ImageType.Primary)
            {
                if (itemsWithImages.Count == 0)
                {
                    return null;
                }

                return CreateThumbCollage(item, itemsWithImages, outputPath, 960, 540);
            }

            return base.CreateImage(item, itemsWithImages, outputPath, imageType, imageIndex);
        }

        protected override bool HasChangedByDate(BaseItem item, ItemImageInfo image)
        {
            var age = DateTime.UtcNow - image.DateModified;
            return age.TotalDays > 7;
        }
    }
}
