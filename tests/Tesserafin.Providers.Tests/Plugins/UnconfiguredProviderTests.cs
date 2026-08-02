using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Plugins.AudioDb;
using Tesserafin.Providers.Plugins.Omdb;
using Xunit;
using AudioDbPlugin = Tesserafin.Providers.Plugins.AudioDb.Plugin;
using OmdbPlugin = Tesserafin.Providers.Plugins.Omdb.Plugin;

namespace Tesserafin.Providers.Tests.Plugins
{
    /// <summary>
    /// Proves the unconfigured path for <em>every</em> provider class that consumes one of the two
    /// operator-supplied credentials — not only the two the credential helpers are exercised through.
    /// </summary>
    /// <remarks>
    /// Each of these classes reads a cache file that only exists after a successful authenticated
    /// download. With no key configured that download never happens, so each one has to answer with
    /// its architecture's normal empty result rather than dereferencing an absent file or an absent
    /// deserialised object. Every case here asserts both halves: no HTTP request left the process,
    /// and the answer was empty rather than an exception.
    /// </remarks>
    [Collection(ProviderPluginStaticState.Name)]
    public sealed class UnconfiguredProviderTests : IDisposable
    {
        private readonly TempDirectory _root = new();
        private readonly RecordingLogger _logger = new();
        private readonly RecordingHandler _handler = new();

        public UnconfiguredProviderTests()
        {
            AudioDbApi.ResetUnconfiguredWarningLatch();
            OmdbApi.ResetUnconfiguredWarningLatch();
            _ = new AudioDbPlugin(ProviderPluginHarness.ApplicationPaths(_root.Path), ProviderPluginHarness.XmlSerializer());
            _ = new OmdbPlugin(ProviderPluginHarness.ApplicationPaths(_root.Path), ProviderPluginHarness.XmlSerializer());
        }

        public void Dispose()
        {
            AudioDbApi.ResetUnconfiguredWarningLatch();
            OmdbApi.ResetUnconfiguredWarningLatch();
            _handler.Dispose();
            _root.Dispose();
        }

        [Fact]
        public async Task AudioDbAlbumProvider_WithNoConfiguredKey_IssuesNoRequestAndReturnsEmptyResult()
        {
            var provider = new AudioDbAlbumProvider(
                ServerConfiguration(),
                FileSystem(),
                ProviderPluginHarness.HttpClientFactory(_handler),
                new RecordingLogger<AudioDbAlbumProvider>(_logger));

            var info = new AlbumInfo();
            info.ProviderIds[MetadataProvider.MusicBrainzReleaseGroup.ToString()] = Guid.NewGuid().ToString("N");

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.Empty(_handler.Requests);
            Assert.False(result.HasMetadata);
        }

        [Fact]
        public async Task AudioDbArtistImageProvider_WithNoConfiguredKey_IssuesNoRequestAndOffersNoImages()
        {
            // The image provider reaches through AudioDbArtistProvider.Current, so one has to exist.
            _ = new AudioDbArtistProvider(
                ServerConfiguration(),
                FileSystem(),
                ProviderPluginHarness.HttpClientFactory(_handler),
                new RecordingLogger<AudioDbArtistProvider>(_logger));

            var provider = new AudioDbArtistImageProvider(
                ServerConfiguration(),
                FileSystem(),
                ProviderPluginHarness.HttpClientFactory(_handler));

            var artist = new MusicArtist();
            artist.SetProviderId(MetadataProvider.MusicBrainzArtist, Guid.NewGuid().ToString("N"));

            var images = await provider.GetImages(artist, CancellationToken.None);

            Assert.Empty(_handler.Requests);
            Assert.Empty(images);
        }

        [Fact]
        public async Task AudioDbAlbumImageProvider_WithNoConfiguredKey_IssuesNoRequestAndOffersNoImages()
        {
            _ = new AudioDbAlbumProvider(
                ServerConfiguration(),
                FileSystem(),
                ProviderPluginHarness.HttpClientFactory(_handler),
                new RecordingLogger<AudioDbAlbumProvider>(_logger));

            var provider = new AudioDbAlbumImageProvider(
                ServerConfiguration(),
                FileSystem(),
                ProviderPluginHarness.HttpClientFactory(_handler));

            var album = new MusicAlbum();
            album.SetProviderId(MetadataProvider.MusicBrainzReleaseGroup, Guid.NewGuid().ToString("N"));

            var images = await provider.GetImages(album, CancellationToken.None);

            Assert.Empty(_handler.Requests);
            Assert.Empty(images);
        }

        [Fact]
        public async Task OmdbImageProvider_WithNoConfiguredKey_IssuesNoRequestAndOffersNoImages()
        {
            var provider = new OmdbImageProvider(
                ProviderPluginHarness.HttpClientFactory(_handler),
                FileSystem(),
                ServerConfiguration(),
                new RecordingLogger<OmdbImageProvider>(_logger));

            var movie = new Movie();
            movie.SetProviderId(MetadataProvider.Imdb, "tt0133093");

            var images = await provider.GetImages(movie, CancellationToken.None);

            Assert.Empty(_handler.Requests);
            Assert.Empty(images);
        }

        [Fact]
        public async Task OmdbEpisodeProvider_WithNoConfiguredKey_IssuesNoRequestAndReturnsEmptyResult()
        {
            var provider = new OmdbEpisodeProvider(
                ProviderPluginHarness.HttpClientFactory(_handler),
                ItemNamingService(),
                FileSystem(),
                ServerConfiguration(),
                LoggerFactory());

            var info = new EpisodeInfo { IndexNumber = 1, ParentIndexNumber = 1 };
            info.SeriesProviderIds[MetadataProvider.Imdb.ToString()] = "tt0903747";

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.Empty(_handler.Requests);
            Assert.False(result.HasMetadata);
        }

        [Fact]
        public void EveryUnconfiguredLookup_EmitsAtMostOneWarningPerProvider()
        {
            Assert.False(AudioDbApi.TryGetBaseUrl(_logger, out _));
            Assert.False(AudioDbApi.TryGetBaseUrl(_logger, out _));
            Assert.False(OmdbApi.TryGetRequestUrl("i=tt0133093", _logger, out _));
            Assert.False(OmdbApi.TryGetRequestUrl("i=tt0133093", _logger, out _));

            Assert.Equal(2, _logger.Entries.Count);
        }

        private IServerConfigurationManager ServerConfiguration()
        {
            var paths = new Mock<IServerApplicationPaths>();
            paths.SetupGet(p => p.CachePath).Returns(System.IO.Path.Combine(_root.Path, "cache"));
            var manager = new Mock<IServerConfigurationManager>();
            manager.SetupGet(m => m.ApplicationPaths).Returns(paths.Object);
            return manager.Object;
        }

        private static IFileSystem FileSystem()
        {
            var fileSystem = new Mock<IFileSystem>();
            fileSystem
                .Setup(f => f.GetFileSystemInfo(It.IsAny<string>()))
                .Returns((string path) => new FileSystemMetadata { FullName = path, Exists = false });
            return fileSystem.Object;
        }

        private static IItemNamingService ItemNamingService()
        {
            var service = new Mock<IItemNamingService>();
            service.Setup(s => s.ParseName(It.IsAny<string>())).Returns((string name) => new ItemLookupInfo { Name = name });
            return service.Object;
        }

        private Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory()
        {
            var factory = new Mock<Microsoft.Extensions.Logging.ILoggerFactory>();
            factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger);
            return factory.Object;
        }
    }
}
