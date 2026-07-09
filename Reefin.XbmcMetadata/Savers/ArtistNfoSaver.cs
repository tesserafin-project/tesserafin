using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Extensions.Logging;
using Reefin.Controller.Configuration;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Library;
using Reefin.Model.IO;
using Reefin.XbmcMetadata.Configuration;

namespace Reefin.XbmcMetadata.Savers
{
    /// <summary>
    /// Nfo saver for artist.
    /// </summary>
    public class ArtistNfoSaver : BaseNfoSaver
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ArtistNfoSaver"/> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="configurationManager">the server configuration manager.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="itemPeopleService">The item people service.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="userDataManager">The user data manager.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="mediaSourceManager">The media source manager.</param>
        public ArtistNfoSaver(
            IFileSystem fileSystem,
            IServerConfigurationManager configurationManager,
            ILibraryManager libraryManager,
            IItemPeopleService itemPeopleService,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILogger<ArtistNfoSaver> logger,
            IMediaSourceManager mediaSourceManager)
            : base(fileSystem, configurationManager, libraryManager, itemPeopleService, userManager, userDataManager, logger, mediaSourceManager)
        {
        }

        /// <inheritdoc />
        protected override string GetLocalSavePath(BaseItem item)
            => Path.Combine(item.Path, "artist.nfo");

        /// <inheritdoc />
        protected override string GetRootElementName(BaseItem item)
            => "artist";

        /// <inheritdoc />
        public override bool IsEnabledFor(BaseItem item, ItemUpdateType updateType)
            => item.SupportsLocalMetadata && item is MusicArtist && updateType >= MinimumUpdateType;

        /// <inheritdoc />
        protected override void WriteCustomElements(BaseItem item, XmlWriter writer)
        {
            var artist = (MusicArtist)item;

            if (artist.EndDate.HasValue)
            {
                var formatString = ConfigurationManager.GetNfoConfiguration().ReleaseDateFormat;

                writer.WriteElementString("disbanded", artist.EndDate.Value.ToString(formatString, CultureInfo.InvariantCulture));
            }

            var albums = artist
                .GetRecursiveChildren(i => i is MusicAlbum);

            AddAlbums(albums, writer);
        }

        private void AddAlbums(IReadOnlyList<BaseItem> albums, XmlWriter writer)
        {
            foreach (var album in albums
                .OrderBy(album => album.ProductionYear ?? 0)
                .ThenBy(album => SortNameOrName(album))
                .ThenBy(album => album.Name?.Trim()))
            {
                writer.WriteStartElement("album");

                if (!string.IsNullOrEmpty(album.Name))
                {
                    writer.WriteElementString("title", album.Name);
                }

                if (album.ProductionYear.HasValue)
                {
                    writer.WriteElementString("year", album.ProductionYear.Value.ToString(CultureInfo.InvariantCulture));
                }

                writer.WriteEndElement();
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
            yield return "disbanded";
        }
    }
}
