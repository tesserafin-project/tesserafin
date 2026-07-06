using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Reefin.Controller;
using Reefin.Controller.Configuration;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Library;
using Reefin.Controller.LiveTv;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Entities;
using Reefin.Model.Globalization;
using Reefin.Model.IO;
using Reefin.Naming.Common;
using Reefin.Providers.MediaInfo;
using Xunit;

namespace Reefin.Providers.Tests.MediaInfo;

public class AudioResolverTests
{
    private readonly AudioResolver _audioResolver;

    public AudioResolverTests()
    {
        // prep BaseItem and Video for calls made that expect managers
        Video.RecordingsManager = Mock.Of<IRecordingsManager>();

        var applicationPaths = new Mock<IServerApplicationPaths>().Object;
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.ApplicationPaths)
            .Returns(applicationPaths);
        BaseItem.ConfigurationManager = serverConfig.Object;

        // build resolver to test with
        var localizationManager = Mock.Of<ILocalizationManager>();

        var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        mediaEncoder.Setup(me => me.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .Returns<MediaInfoRequest, CancellationToken>((_, _) => Task.FromResult(new Reefin.Model.MediaInfo.MediaInfo
            {
                MediaStreams = new List<MediaStream>
                {
                    new()
                    {
                        Type = MediaStreamType.Audio
                    }
                }
            }));

        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(fs => fs.DirectoryExists(It.IsRegex(MediaInfoResolverTests.VideoDirectoryRegex)))
            .Returns(true);
        fileSystem.Setup(fs => fs.DirectoryExists(It.IsRegex(MediaInfoResolverTests.MetadataDirectoryRegex)))
            .Returns(true);

        _audioResolver = new AudioResolver(Mock.Of<ILogger<AudioResolver>>(), localizationManager, mediaEncoder.Object, fileSystem.Object, new NamingOptions());
    }

    [Theory]
    [InlineData("My.Video.srt", false, false)]
    [InlineData("My.Video.mp3", false, true)]
    [InlineData("My.Video.srt", true, false)]
    [InlineData("My.Video.mp3", true, true)]
    public async Task GetExternalStreams_MixedFilenames_PicksAudio(string file, bool metadataDirectory, bool matches)
    {
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>();

        var video = new Movie
        {
            Path = MediaInfoResolverTests.VideoDirectoryPath + "/My.Video.mkv"
        };

        var directoryService = MediaInfoResolverTests.GetDirectoryServiceForExternalFile(file, metadataDirectory);
        var streams = await _audioResolver.GetExternalStreamsAsync(video, 0, directoryService, false, CancellationToken.None);

        if (matches)
        {
            Assert.Single(streams);
            var actual = streams[0];
            Assert.Equal(MediaStreamType.Audio, actual.Type);
        }
        else
        {
            Assert.Empty(streams);
        }
    }
}
