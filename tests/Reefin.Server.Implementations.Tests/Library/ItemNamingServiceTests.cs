using System;
using Moq;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.TV;
using Reefin.Controller.Library;
using Reefin.Model.Entities;
using Reefin.Model.MediaInfo;
using Reefin.Naming.Common;
using Reefin.Server.Core.Library;
using Xunit;

namespace Reefin.Server.Implementations.Tests.Library;

public class ItemNamingServiceTests
{
    private static ItemNamingService CreateService()
        => new(new NamingOptions());

    [Fact]
    public void ParseName_CleansNameAndYear()
    {
        var service = CreateService();

        var result = service.ParseName("The Movie (2020)");

        Assert.Equal("The Movie", result.Name);
        Assert.Equal(2020, result.Year);
    }

    [Fact]
    public void GetSeasonNumberFromPath_UsesParentPathContext()
    {
        var service = CreateService();

        var result = service.GetSeasonNumberFromPath("/media/Shows/Example/Season 02", "/media/Shows/Example");

        Assert.Equal(2, result);
    }

    [Fact]
    public void FillMissingEpisodeNumbersFromPath_PopulatesSeasonAndEpisodeNumbers()
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(x => x.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        BaseItem.MediaSourceManager = mediaSourceManager.Object;

        var service = CreateService();
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Path = "/media/Shows/Example/S01E02.mkv",
            VideoType = VideoType.VideoFile
        };

        var result = service.FillMissingEpisodeNumbersFromPath(episode, false);

        Assert.True(result);
        Assert.Equal(1, episode.ParentIndexNumber);
        Assert.Equal(2, episode.IndexNumber);
    }
}
