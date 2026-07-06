using System.Threading;
using Microsoft.Extensions.Logging;
using Reefin.Controller.Playlists;
using Reefin.Controller.Providers;
using Reefin.LocalMetadata.Parsers;
using Reefin.LocalMetadata.Savers;
using Reefin.Model.IO;

namespace Reefin.LocalMetadata.Providers
{
    /// <summary>
    /// Playlist xml provider.
    /// </summary>
    public class PlaylistXmlProvider : BaseXmlProvider<Playlist>
    {
        private readonly ILogger<PlaylistXmlParser> _logger;
        private readonly IProviderManager _providerManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlaylistXmlProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{PlaylistXmlParser}"/> interface.</param>
        /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
        public PlaylistXmlProvider(
            IFileSystem fileSystem,
            ILogger<PlaylistXmlParser> logger,
            IProviderManager providerManager)
            : base(fileSystem)
        {
            _logger = logger;
            _providerManager = providerManager;
        }

        /// <inheritdoc />
        protected override void Fetch(MetadataResult<Playlist> result, string path, CancellationToken cancellationToken)
        {
            new PlaylistXmlParser(_logger, _providerManager).Fetch(result, path, cancellationToken);
        }

        /// <inheritdoc />
        protected override FileSystemMetadata? GetXmlFile(ItemInfo info, IDirectoryService directoryService)
        {
            return directoryService.GetFile(PlaylistXmlSaver.GetSavePath(info.Path));
        }
    }
}
