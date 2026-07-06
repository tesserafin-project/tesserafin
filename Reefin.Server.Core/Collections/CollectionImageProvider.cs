using System.Collections.Generic;
using System.Linq;
using Reefin.Common.Configuration;
using Reefin.Controller.Drawing;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Entities.TV;
using Reefin.Controller.Providers;
using Reefin.Model.Entities;
using Reefin.Model.IO;
using Reefin.Server.Core.Images;

namespace Reefin.Server.Core.Collections
{
    /// <summary>
    /// A collection image provider.
    /// </summary>
    public class CollectionImageProvider : BaseDynamicImageProvider<BoxSet>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CollectionImageProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">The filesystem.</param>
        /// <param name="providerManager">The provider manager.</param>
        /// <param name="applicationPaths">The application paths.</param>
        /// <param name="imageProcessor">The image processor.</param>
        public CollectionImageProvider(
            IFileSystem fileSystem,
            IProviderManager providerManager,
            IApplicationPaths applicationPaths,
            IImageProcessor imageProcessor)
            : base(fileSystem, providerManager, applicationPaths, imageProcessor)
        {
        }

        /// <inheritdoc />
        protected override bool Supports(BaseItem item)
        {
            // Right now this is the only way to prevent this image from getting created ahead of internet image providers
            if (!item.IsLocked)
            {
                return false;
            }

            return base.Supports(item);
        }

        /// <inheritdoc />
        protected override IReadOnlyList<BaseItem> GetItemsWithImages(BaseItem item)
        {
            var playlist = (BoxSet)item;

            return playlist.Children.Concat(playlist.GetLinkedChildren())
                .Select(i =>
                {
                    var subItem = i;

                    var episode = subItem as Episode;

                    var series = episode?.Series;
                    if (series is not null && series.HasImage(ImageType.Primary))
                    {
                        return series;
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
                .GroupBy(x => x!.Id) // We removed the null values
                .Select(x => x.First())
                .ToList()!; // Again... the list doesn't contain any null values
        }

        /// <inheritdoc />
        protected override string CreateImage(BaseItem item, IReadOnlyCollection<BaseItem> itemsWithImages, string outputPathWithoutExtension, ImageType imageType, int imageIndex)
        {
            return CreateSingleImage(itemsWithImages, outputPathWithoutExtension, ImageType.Primary);
        }
    }
}
