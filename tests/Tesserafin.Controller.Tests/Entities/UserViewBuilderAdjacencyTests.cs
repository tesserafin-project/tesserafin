using System;
using System.Collections.Generic;
using System.Linq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

public class UserViewBuilderAdjacencyTests
{
    [Fact]
    public void FilterForAdjacency_AnchorNotInVisibleEpisodes_ReturnsEmpty()
    {
        List<BaseItem> episodes = [new Episode { Id = Guid.NewGuid() }];

        Assert.Empty(UserViewBuilder.FilterForAdjacency(episodes, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(1, 0, 3)]
    [InlineData(2, 1, 2)]
    public void FilterForAdjacency_VisibleAnchor_PreservesOrderedNeighbors(int anchorIndex, int firstIndex, int count)
    {
        List<BaseItem> episodes =
        [
            new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 1 },
            new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 2 },
            new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 2, IndexNumber = 1 },
        ];

        var result = UserViewBuilder.FilterForAdjacency(episodes, episodes[anchorIndex].Id);

        Assert.Equal(episodes.Skip(firstIndex).Take(count).Select(item => item.Id), result.Select(item => item.Id));
    }
}
