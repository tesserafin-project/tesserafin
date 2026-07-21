using System;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;
using Tesserafin.Providers.Plugins.MusicBrainz;
using Tesserafin.XbmcMetadata.Parsers;
using Xunit;

namespace Tesserafin.XbmcMetadata.Tests.Parsers
{
    public class MusicArtistNfoParserTests
    {
        private readonly BaseNfoParser<MusicArtist> _parser;

        public MusicArtistNfoParserTests()
        {
            var providerManager = new Mock<IProviderManager>();

            var musicBrainzArtist = new MusicBrainzArtistExternalId();
            var externalIdInfo = new ExternalIdInfo(musicBrainzArtist.ProviderName, musicBrainzArtist.Key, musicBrainzArtist.Type);

            providerManager.Setup(x => x.GetExternalIdInfos(It.IsAny<IHasProviderIds>()))
                .Returns(new[] { externalIdInfo });

            var config = new Mock<IConfigurationManager>();
            config.Setup(x => x.GetConfiguration(It.IsAny<string>()))
                .Returns(new XbmcMetadataOptions());
            var user = new Mock<IUserManager>();
            var userData = new Mock<IUserDataManager>();
            var directoryService = new Mock<IDirectoryService>();

            _parser = new BaseNfoParser<MusicArtist>(
                new NullLogger<BaseNfoParser<MusicArtist>>(),
                config.Object,
                providerManager.Object,
                user.Object,
                userData.Object,
                directoryService.Object);
        }

        [Fact]
        public void Fetch_Valid_Success()
        {
            var result = new MetadataResult<MusicArtist>()
            {
                Item = new MusicArtist()
            };

            _parser.Fetch(result, "Test Data/U2.nfo", CancellationToken.None);
            var item = result.Item;

            Assert.Equal("U2", item.Name);
            Assert.Equal("U2", item.SortName);
            Assert.Equal("a3cb23fc-acd3-4ce0-8f36-1e5aa6a18432", item.ProviderIds[MetadataProvider.MusicBrainzArtist.ToString()]);

            Assert.Single(item.Genres);
            Assert.Equal("Rock", item.Genres[0]);
        }

        [Fact]
        public void Fetch_WithNullItem_ThrowsArgumentException()
        {
            var result = new MetadataResult<MusicArtist>();

            Assert.Throws<ArgumentException>(() => _parser.Fetch(result, "Test Data/U2.nfo", CancellationToken.None));
        }

        [Fact]
        public void Fetch_NullResult_ThrowsArgumentException()
        {
            var result = new MetadataResult<MusicArtist>()
            {
                Item = new MusicArtist()
            };

            Assert.Throws<ArgumentException>(() => _parser.Fetch(result, string.Empty, CancellationToken.None));
        }
    }
}
