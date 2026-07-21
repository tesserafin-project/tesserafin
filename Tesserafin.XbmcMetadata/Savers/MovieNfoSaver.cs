using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Extensions;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.XbmcMetadata.Savers
{
    /// <summary>
    /// Nfo saver for movies.
    /// </summary>
    public class MovieNfoSaver : BaseNfoSaver
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MovieNfoSaver"/> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="configurationManager">the server configuration manager.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="itemPeopleService">The item people service.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="userDataManager">The user data manager.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="mediaSourceManager">The media source manager.</param>
        public MovieNfoSaver(
            IFileSystem fileSystem,
            IServerConfigurationManager configurationManager,
            ILibraryManager libraryManager,
            IItemPeopleService itemPeopleService,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILogger<MovieNfoSaver> logger,
            IMediaSourceManager mediaSourceManager)
            : base(fileSystem, configurationManager, libraryManager, itemPeopleService, userManager, userDataManager, logger, mediaSourceManager)
        {
        }

        /// <inheritdoc />
        protected override string GetLocalSavePath(BaseItem item)
            => GetMovieSavePaths(new ItemInfo(item)).FirstOrDefault() ?? Path.ChangeExtension(item.Path, ".nfo");

        internal static IEnumerable<string> GetMovieSavePaths(ItemInfo item)
        {
            var path = item.ContainingFolderPath;
            if (item.VideoType == VideoType.Dvd && !item.IsPlaceHolder)
            {
                yield return Path.Combine(path, "VIDEO_TS", "VIDEO_TS.nfo");
            }

            // only allow movie object to read movie.nfo, not owned videos (which will be itemtype video, not movie)
            if (!item.IsInMixedFolder && item.ItemType == typeof(Movie))
            {
                yield return Path.Combine(path, "movie.nfo");
            }

            if (!item.IsPlaceHolder && (item.VideoType == VideoType.Dvd || item.VideoType == VideoType.BluRay))
            {
                yield return Path.Combine(path, Path.GetFileName(path) + ".nfo");
            }
            else
            {
                yield return Path.ChangeExtension(item.Path, ".nfo");
            }
        }

        /// <inheritdoc />
        protected override string GetRootElementName(BaseItem item)
            => item is MusicVideo ? "musicvideo" : "movie";

        /// <inheritdoc />
        public override bool IsEnabledFor(BaseItem item, ItemUpdateType updateType)
        {
            if (!item.SupportsLocalMetadata)
            {
                return false;
            }

            // Check parent for null to avoid running this against things like video backdrops
            if (item is Video video && item is not Episode && !video.ExtraType.HasValue)
            {
                return updateType >= MinimumUpdateType;
            }

            return false;
        }

        /// <inheritdoc />
        protected override void WriteCustomElements(BaseItem item, XmlWriter writer)
        {
            if (item.TryGetProviderId(MetadataProvider.Imdb, out var imdb))
            {
                writer.WriteElementString("id", imdb);
            }

            if (item is MusicVideo musicVideo)
            {
                foreach (var artist in musicVideo.Artists.Trimmed().OrderBy(artist => artist))
                {
                    writer.WriteElementString("artist", artist);
                }

                if (!string.IsNullOrEmpty(musicVideo.Album))
                {
                    writer.WriteElementString("album", musicVideo.Album);
                }
            }

            if (item is Movie movie)
            {
                if (!string.IsNullOrEmpty(movie.CollectionName))
                {
                    writer.WriteStartElement("set");
                    writer.WriteElementString("name", movie.CollectionName);
                    writer.WriteEndElement();
                }
            }
        }

        /// <inheritdoc />
        protected override IEnumerable<string> GetTagsUsed(BaseItem item)
        {
            foreach (var tag in base.GetTagsUsed(item))
            {
                yield return tag;
            }

            yield return "album";
            yield return "artist";
            yield return "set";
            yield return "id";
        }
    }
}
