using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Plugins.AudioDb;
using Tesserafin.Providers.Tests.Plugins;
using Xunit;

namespace Tesserafin.Providers.Tests.AudioDb
{
    /// <summary>
    /// Covers the unconfigured TheAudioDB path.
    /// </summary>
    /// <remarks>
    /// Tesserafin ships no TheAudioDB credential, so a server that has never had one supplied resolves
    /// these providers with nothing to authenticate with. TheAudioDB carries its key as a URL path
    /// segment, so an unconfigured request is not merely unauthenticated — there is no request to
    /// make at all. That path had no coverage while an inherited key was compiled in.
    /// </remarks>
    [Collection(ProviderPluginStaticState.Name)]
    public sealed class AudioDbApiTests : IDisposable
    {
        private readonly TempDirectory _root = new();
        private readonly RecordingLogger _logger = new();

        public AudioDbApiTests()
        {
            AudioDbApi.ResetUnconfiguredWarningLatch();
            CreatePlugin();
        }

        public void Dispose()
        {
            AudioDbApi.ResetUnconfiguredWarningLatch();
            _root.Dispose();
        }

        [Fact]
        public void TryGetBaseUrl_WithNoConfiguredKey_ReturnsFalseAndNoUrl()
        {
            Assert.False(AudioDbApi.IsConfigured);
            Assert.False(AudioDbApi.TryGetBaseUrl(_logger, out var baseUrl));
            Assert.Null(baseUrl);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("\t")]
        [InlineData("   \r\n ")]
        public void TryGetBaseUrl_WithBlankKey_BehavesAsMissing(string configured)
        {
            Configure(configured);

            Assert.False(AudioDbApi.IsConfigured);
            Assert.False(AudioDbApi.TryGetBaseUrl(_logger, out var baseUrl));
            Assert.Null(baseUrl);
        }

        [Fact]
        public void TryGetBaseUrl_WithConfiguredKey_BuildsTheAuthenticatedRoot()
        {
            var key = ProviderPluginHarness.SyntheticKey(6);
            Configure(key);

            Assert.True(AudioDbApi.IsConfigured);
            Assert.True(AudioDbApi.TryGetBaseUrl(_logger, out var baseUrl));
            Assert.Equal(AudioDbApi.ApiRoot + "/" + key, baseUrl);
            Assert.Empty(_logger.Entries);
        }

        [Fact]
        public void TryGetBaseUrl_WithSurroundingWhitespace_UsesTheTrimmedKey()
        {
            var key = ProviderPluginHarness.SyntheticKey(6);
            Configure("  " + key + "\n");

            Assert.True(AudioDbApi.TryGetBaseUrl(_logger, out var baseUrl));
            Assert.Equal(AudioDbApi.ApiRoot + "/" + key, baseUrl);
        }

        [Fact]
        public void TryGetBaseUrl_WithNoConfiguredKey_WarnsExactlyOnce()
        {
            for (var i = 0; i < 5; i++)
            {
                Assert.False(AudioDbApi.TryGetBaseUrl(_logger, out _));
            }

            var warning = Assert.Single(_logger.Entries);
            Assert.Equal(LogLevel.Warning, warning.Level);
        }

        [Fact]
        public void TryGetBaseUrl_WithNoConfiguredKey_NamesTheSettingAndNothingElse()
        {
            Assert.False(AudioDbApi.TryGetBaseUrl(_logger, out _));

            var message = Assert.Single(_logger.Entries).Message;

            // Actionable: it says which product surface to use.
            Assert.Contains("TheAudioDB API key", message, StringComparison.Ordinal);
            Assert.Contains("AudioDB plugin configuration page", message, StringComparison.Ordinal);
            // Bounded: it promises nothing about unrelated providers breaking.
            Assert.Contains("Other metadata providers are unaffected", message, StringComparison.Ordinal);
        }

        [Fact]
        public void ConfiguredKey_NeverAppearsInTheDiagnostic()
        {
            var key = ProviderPluginHarness.SyntheticKey(6);
            Configure(key);

            Assert.True(AudioDbApi.TryGetBaseUrl(_logger, out _));

            // Force the unconfigured path afterwards: the warning must still not quote a value.
            Configure(string.Empty);
            Assert.False(AudioDbApi.TryGetBaseUrl(_logger, out _));

            Assert.All(_logger.Entries, entry => Assert.DoesNotContain(key, entry.Message, StringComparison.Ordinal));
        }

        [Fact]
        public async Task ArtistProvider_WithNoConfiguredKey_IssuesNoRequestAndReturnsEmptyResult()
        {
            var handler = new RecordingHandler();
            var provider = new AudioDbArtistProvider(
                ServerConfiguration(),
                FileSystem(),
                ProviderPluginHarness.HttpClientFactory(handler),
                new RecordingLogger<AudioDbArtistProvider>(_logger));

            var info = new ArtistInfo();
            info.ProviderIds[MetadataProvider.MusicBrainzArtist.ToString()] = Guid.NewGuid().ToString("N");

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.False(result.HasMetadata);
        }

        [Fact]
        public async Task ArtistProvider_WithConfiguredKey_ReachesTheMockedRequestCarryingThatKey()
        {
            var key = ProviderPluginHarness.SyntheticKey(6);
            Configure(key);

            var handler = new RecordingHandler("{\"artists\":null}");
            var provider = new AudioDbArtistProvider(
                ServerConfiguration(),
                FileSystem(),
                ProviderPluginHarness.HttpClientFactory(handler),
                new RecordingLogger<AudioDbArtistProvider>(_logger));

            var musicBrainzId = Guid.NewGuid().ToString("N");
            await provider.DownloadArtistInfo(musicBrainzId, CancellationToken.None);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("www.theaudiodb.com", request.Host);
            Assert.Contains("/" + key + "/", request.AbsolutePath, StringComparison.Ordinal);
            Assert.All(_logger.Entries, entry => Assert.DoesNotContain(key, entry.Message, StringComparison.Ordinal));
        }

        /// <summary>
        /// Guards the disposition of the inherited upstream credential: no compile-time default may
        /// return, under this name or any other. Values are described by shape rather than quoted, so
        /// this assertion does not reintroduce a literal into the tree.
        /// </summary>
        [Fact]
        public void AudioDbArtistProvider_DeclaresNoCredentialConstant()
        {
            Assert.Null(typeof(AudioDbArtistProvider).GetField("ApiKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
            Assert.Null(typeof(AudioDbArtistProvider).GetField("BaseUrl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }

        [Fact]
        public void AudioDbTypes_DeclareNoCredentialBearingConstant()
        {
            var constants = new[] { typeof(AudioDbArtistProvider), typeof(AudioDbAlbumProvider), typeof(AudioDbApi) }
                .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string?)field.GetRawConstantValue())
                .Where(value => value is not null)
                .ToArray();

            // The only surviving URL constant is the anonymous API root: it must end at the version
            // path segment, with nothing following it that could be a key.
            Assert.All(constants, value => Assert.DoesNotMatch(@"theaudiodb\.com/api/v1/json/.+", value!));
        }

        private void CreatePlugin()
            => _ = new Plugin(ProviderPluginHarness.ApplicationPaths(_root.Path), ProviderPluginHarness.XmlSerializer());

        private void Configure(string apiKey)
            => Plugin.Instance.UpdateConfiguration(new PluginConfiguration { AudioDbApiKey = apiKey });

        private IServerConfigurationManager ServerConfiguration()
        {
            var paths = new Mock<IServerApplicationPaths>();
            paths.SetupGet(p => p.CachePath).Returns(System.IO.Path.Combine(_root.Path, "cache"));
            var manager = new Mock<IServerConfigurationManager>();
            manager.SetupGet(m => m.ApplicationPaths).Returns(paths.Object);
            return manager.Object;
        }

        private IFileSystem FileSystem()
        {
            var fileSystem = new Mock<IFileSystem>();
            fileSystem
                .Setup(f => f.GetFileSystemInfo(It.IsAny<string>()))
                .Returns((string path) => new FileSystemMetadata { FullName = path, Exists = false });
            return fileSystem.Object;
        }
    }
}
