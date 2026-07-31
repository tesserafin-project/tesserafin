using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Providers.Plugins.Tmdb;
using TMDbLib.Objects.Find;
using Xunit;

namespace Tesserafin.Providers.Tests.Tmdb
{
    /// <summary>
    /// Covers the unconfigured TheMovieDb path. Tesserafin ships no built-in API key, so a server that
    /// has never had one supplied resolves this manager with nothing to authenticate with. That path had
    /// no coverage while an inherited key was compiled in, and it is the path that breaks loudly if the
    /// key is simply deleted: <c>new TMDbClient("")</c> throws <see cref="ArgumentException"/>.
    /// </summary>
    public class TmdbClientManagerTests
    {
        private static TmdbClientManager CreateUnconfiguredManager()
            => new TmdbClientManager(new MemoryCache(new MemoryCacheOptions()), NullLogger<TmdbClientManager>.Instance);

        [Fact]
        public void Constructor_WithoutConfiguredApiKey_DoesNotThrow()
        {
            using var manager = CreateUnconfiguredManager();

            Assert.False(manager.IsConfigured);
        }

        [Fact]
        public async Task GetMovieAsync_WithoutConfiguredApiKey_ReturnsNull()
        {
            using var manager = CreateUnconfiguredManager();

            Assert.Null(await manager.GetMovieAsync(603, "en", null, null, CancellationToken.None));
        }

        [Fact]
        public async Task FindByExternalIdAsync_WithoutConfiguredApiKey_ReturnsNull()
        {
            using var manager = CreateUnconfiguredManager();

            Assert.Null(await manager.FindByExternalIdAsync("tt0133093", FindExternalSource.Imdb, "en", null, CancellationToken.None));
        }

        [Fact]
        public async Task GetMovieSimilarPageAsync_WithoutConfiguredApiKey_ReturnsEmptyPage()
        {
            using var manager = CreateUnconfiguredManager();

            var (results, totalPages) = await manager.GetMovieSimilarPageAsync(603, 1, "en", CancellationToken.None);

            Assert.Empty(results);
            Assert.Equal(0, totalPages);
        }

        [Fact]
        public async Task GetClientConfiguration_WithoutConfiguredApiKey_ReturnsNull()
        {
            using var manager = CreateUnconfiguredManager();

            Assert.Null(await manager.GetClientConfiguration());
        }

        /// <summary>
        /// Guards the disposition of the inherited upstream credential: no compile-time default may
        /// return. The credential-shaped value is described by its shape rather than quoted, so this
        /// assertion does not reintroduce a literal into the tree.
        /// </summary>
        [Fact]
        public void TmdbUtils_DeclaresNoApiKeyConstant()
        {
            Assert.Null(typeof(TmdbUtils).GetField("ApiKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }

        [Fact]
        public void TmdbUtils_DeclaresNoCredentialShapedConstant()
        {
            var constants = typeof(TmdbUtils)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string?)field.GetRawConstantValue())
                .Where(value => value is not null)
                .ToArray();

            Assert.All(constants, value => Assert.DoesNotMatch("^[0-9a-f]{32}$", value!));
        }
    }
}
