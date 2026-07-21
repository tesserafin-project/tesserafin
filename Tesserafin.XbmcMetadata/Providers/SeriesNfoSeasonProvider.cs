using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;
using Tesserafin.XbmcMetadata.Parsers;

namespace Tesserafin.XbmcMetadata.Providers
{
    /// <summary>
    /// NFO provider for seasons based on series NFO.
    /// </summary>
    public class SeriesNfoSeasonProvider : BaseNfoProvider<Season>
    {
        private readonly ILogger<SeriesNfoSeasonProvider> _logger;
        private readonly IConfigurationManager _config;
        private readonly IProviderManager _providerManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IDirectoryService _directoryService;
        private readonly IItemLookupService _itemLookupService;

        /// <summary>
        /// Initializes a new instance of the <see cref="SeriesNfoSeasonProvider"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger{SeasonFromSeriesNfoProvider}"/> interface.</param>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="config">Instance of the <see cref="IConfigurationManager"/> interface.</param>
        /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
        /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
        /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
        /// <param name="directoryService">Instance of the <see cref="IDirectoryService"/> interface.</param>
        /// <param name="itemLookupService">Instance of the <see cref="IItemLookupService"/> interface.</param>
        public SeriesNfoSeasonProvider(
            ILogger<SeriesNfoSeasonProvider> logger,
            IFileSystem fileSystem,
            IConfigurationManager config,
            IProviderManager providerManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            IDirectoryService directoryService,
            IItemLookupService itemLookupService)
            : base(fileSystem)
        {
            _logger = logger;
            _config = config;
            _providerManager = providerManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _directoryService = directoryService;
            _itemLookupService = itemLookupService;
        }

        /// <inheritdoc />
        protected override void Fetch(MetadataResult<Season> result, string path, CancellationToken cancellationToken)
        {
            new SeriesNfoSeasonParser(_logger, _config, _providerManager, _userManager, _userDataManager, _directoryService).Fetch(result, path, cancellationToken);
        }

        /// <inheritdoc />
        protected override FileSystemMetadata? GetXmlFile(ItemInfo info, IDirectoryService directoryService)
        {
            var seasonPath = info.Path;
            if (seasonPath is not null)
            {
                var path = Path.Combine(seasonPath, "tvshow.nfo");
                if (Path.Exists(path))
                {
                    return directoryService.GetFile(path);
                }
            }

            var seriesPath = _itemLookupService.GetItemById(info.ParentId)?.Path;
            if (seriesPath is not null)
            {
                var path = Path.Combine(seriesPath, "tvshow.nfo");
                if (Path.Exists(path))
                {
                    return directoryService.GetFile(path);
                }
            }

            return null;
        }
    }
}
