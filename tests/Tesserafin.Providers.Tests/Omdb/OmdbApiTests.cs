using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Plugins.Omdb;
using Tesserafin.Providers.Tests.Plugins;
using Xunit;

namespace Tesserafin.Providers.Tests.Omdb
{
    /// <summary>
    /// Covers the unconfigured OMDb path.
    /// </summary>
    /// <remarks>
    /// Tesserafin ships no OMDb credential. OMDb has no anonymous tier, so with no operator key there
    /// is no request to issue and the correct answer is an empty result — not a failed HTTP call and
    /// not an exception. That path had no coverage while an inherited key was compiled into the
    /// request URL.
    /// </remarks>
    [Collection(ProviderPluginStaticState.Name)]
    public sealed class OmdbApiTests : IDisposable
    {
        private readonly TempDirectory _root = new();
        private readonly RecordingLogger _logger = new();

        public OmdbApiTests()
        {
            OmdbApi.ResetUnconfiguredWarningLatch();
            _ = new Plugin(ProviderPluginHarness.ApplicationPaths(_root.Path), ProviderPluginHarness.XmlSerializer());
        }

        public void Dispose()
        {
            OmdbApi.ResetUnconfiguredWarningLatch();
            _root.Dispose();
        }

        [Fact]
        public void TryGetRequestUrl_WithNoConfiguredKey_ReturnsFalseAndNoUrl()
        {
            Assert.False(OmdbApi.IsConfigured);
            Assert.False(OmdbApi.TryGetRequestUrl("i=tt0133093", _logger, out var url));
            Assert.Null(url);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("\t")]
        [InlineData("   \r\n ")]
        public void TryGetRequestUrl_WithBlankKey_BehavesAsMissing(string configured)
        {
            Configure(configured);

            Assert.False(OmdbApi.IsConfigured);
            Assert.False(OmdbApi.TryGetRequestUrl("i=tt0133093", _logger, out var url));
            Assert.Null(url);
        }

        [Fact]
        public void TryGetRequestUrl_WithConfiguredKey_CarriesItInTheApiKeyParameter()
        {
            var key = ProviderPluginHarness.SyntheticKey(8);
            Configure(key);

            Assert.True(OmdbApi.IsConfigured);
            Assert.True(OmdbApi.TryGetRequestUrl("i=tt0133093", _logger, out var url));
            Assert.Equal(OmdbApi.ApiRoot + "?apikey=" + key + "&i=tt0133093", url);
            Assert.Empty(_logger.Entries);
        }

        [Fact]
        public void TryGetRequestUrl_WithNoQuery_ReturnsTheBareAuthenticatedRoot()
        {
            var key = ProviderPluginHarness.SyntheticKey(8);
            Configure(key);

            Assert.True(OmdbApi.TryGetRequestUrl(null, _logger, out var url));
            Assert.Equal(OmdbApi.ApiRoot + "?apikey=" + key, url);
        }

        [Fact]
        public void TryGetRequestUrl_WithNoConfiguredKey_WarnsExactlyOnce()
        {
            for (var i = 0; i < 5; i++)
            {
                Assert.False(OmdbApi.TryGetRequestUrl("i=tt0133093", _logger, out _));
            }

            var warning = Assert.Single(_logger.Entries);
            Assert.Equal(LogLevel.Warning, warning.Level);
        }

        [Fact]
        public void TryGetRequestUrl_WithNoConfiguredKey_NamesTheSettingAndNothingElse()
        {
            Assert.False(OmdbApi.TryGetRequestUrl("i=tt0133093", _logger, out _));

            var message = Assert.Single(_logger.Entries).Message;

            Assert.Contains("OMDb API key", message, StringComparison.Ordinal);
            Assert.Contains("OMDb plugin configuration page", message, StringComparison.Ordinal);
            Assert.Contains("Other metadata providers are unaffected", message, StringComparison.Ordinal);
        }

        [Fact]
        public void ConfiguredKey_NeverAppearsInTheDiagnostic()
        {
            var key = ProviderPluginHarness.SyntheticKey(8);
            Configure(key);
            Assert.True(OmdbApi.TryGetRequestUrl("i=tt0133093", _logger, out _));

            Configure(string.Empty);
            Assert.False(OmdbApi.TryGetRequestUrl("i=tt0133093", _logger, out _));

            Assert.All(_logger.Entries, entry => Assert.DoesNotContain(key, entry.Message, StringComparison.Ordinal));
        }

        [Fact]
        public async Task ItemProvider_WithNoConfiguredKey_IssuesNoRequestAndReturnsNoResults()
        {
            var handler = new RecordingHandler();
            var provider = CreateItemProvider(handler);

            var results = await provider.GetSearchResults(new MovieInfo { Name = "The Matrix", Year = 1999 }, CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.Empty(results);
        }

        [Fact]
        public async Task ItemProvider_WithNoConfiguredKey_DoesNotThrowOnMetadataLookup()
        {
            var handler = new RecordingHandler();
            var provider = CreateItemProvider(handler);

            var info = new MovieInfo { Name = "The Matrix", Year = 1999 };
            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.False(result.HasMetadata);
        }

        [Fact]
        public async Task ItemProvider_WithConfiguredKey_ReachesTheMockedRequestCarryingThatKey()
        {
            var key = ProviderPluginHarness.SyntheticKey(8);
            Configure(key);

            var handler = new RecordingHandler("{\"Response\":\"False\"}");
            var provider = CreateItemProvider(handler);

            await provider.GetSearchResults(new MovieInfo { Name = "The Matrix", Year = 1999 }, CancellationToken.None);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("www.omdbapi.com", request.Host);
            Assert.Contains("apikey=" + key, request.Query, StringComparison.Ordinal);
            Assert.All(_logger.Entries, entry => Assert.DoesNotContain(key, entry.Message, StringComparison.Ordinal));
        }

        /// <summary>
        /// Guards the disposition of the inherited upstream credential: the URL builder that carried
        /// it is gone, and no compile-time default replaced it. The value is described by shape
        /// rather than quoted, so this assertion does not reintroduce a literal into the tree.
        /// </summary>
        [Fact]
        public void OmdbProvider_NoLongerExposesTheCredentialBearingUrlBuilder()
        {
            Assert.Null(typeof(OmdbProvider).GetMethod("GetOmdbUrl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }

        [Fact]
        public void OmdbTypes_DeclareNoCredentialBearingConstant()
        {
            var constants = new[] { typeof(OmdbProvider), typeof(OmdbItemProvider), typeof(OmdbApi) }
                .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string?)field.GetRawConstantValue())
                .Where(value => value is not null)
                .ToArray();

            Assert.All(constants, value => Assert.DoesNotMatch(@"[?&]apikey=.+", value!));
        }

        private void Configure(string apiKey)
            => Plugin.Instance.UpdateConfiguration(new PluginConfiguration { OmdbApiKey = apiKey });

        private OmdbItemProvider CreateItemProvider(RecordingHandler handler)
        {
            var paths = new Mock<IServerApplicationPaths>();
            paths.SetupGet(p => p.CachePath).Returns(System.IO.Path.Combine(_root.Path, "cache"));
            var configurationManager = new Mock<IServerConfigurationManager>();
            configurationManager.SetupGet(m => m.ApplicationPaths).Returns(paths.Object);

            var fileSystem = new Mock<IFileSystem>();
            fileSystem
                .Setup(f => f.GetFileSystemInfo(It.IsAny<string>()))
                .Returns((string path) => new FileSystemMetadata { FullName = path, Exists = false });

            var itemNamingService = new Mock<IItemNamingService>();
            itemNamingService
                .Setup(s => s.ParseName(It.IsAny<string>()))
                .Returns((string name) => new ItemLookupInfo { Name = name });

            return new OmdbItemProvider(
                ProviderPluginHarness.HttpClientFactory(handler),
                itemNamingService.Object,
                fileSystem.Object,
                configurationManager.Object,
                new RecordingLogger<OmdbItemProvider>(_logger));
        }
    }
}
