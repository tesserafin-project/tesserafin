using Moq;
using Reefin.Controller.Persistence;
using Reefin.Model.Entities;
using Reefin.Server.Core.Library;
using Xunit;

namespace Reefin.Server.Implementations.Tests.Library;

public class MediaStreamLanguageServiceTests
{
    private static MediaStreamLanguageService CreateService(out Mock<IMediaStreamRepository> mediaStreamRepository)
    {
        mediaStreamRepository = new Mock<IMediaStreamRepository>();
        return new MediaStreamLanguageService(mediaStreamRepository.Object);
    }

    [Fact]
    public void GetMediaStreamLanguages_Audio_ForwardsToRepository()
    {
        var service = CreateService(out var mediaStreamRepository);
        var expected = new[] { "eng", "fra" };

        mediaStreamRepository
            .Setup(x => x.GetMediaStreamLanguages(MediaStreamType.Audio))
            .Returns(expected);

        var result = service.GetMediaStreamLanguages(MediaStreamType.Audio);

        Assert.Same(expected, result);
        mediaStreamRepository.Verify(x => x.GetMediaStreamLanguages(MediaStreamType.Audio), Times.Once);
    }

    [Fact]
    public void GetMediaStreamLanguages_Subtitle_ForwardsToRepository()
    {
        var service = CreateService(out var mediaStreamRepository);
        var expected = new[] { "eng", "spa" };

        mediaStreamRepository
            .Setup(x => x.GetMediaStreamLanguages(MediaStreamType.Subtitle))
            .Returns(expected);

        var result = service.GetMediaStreamLanguages(MediaStreamType.Subtitle);

        Assert.Same(expected, result);
        mediaStreamRepository.Verify(x => x.GetMediaStreamLanguages(MediaStreamType.Subtitle), Times.Once);
    }
}
