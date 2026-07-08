using System;
using System.Linq;
using Moq;
using Reefin.Controller.Channels;
using Reefin.Controller.Collections;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Library;
using Reefin.Controller.TV;
using Xunit;

namespace Reefin.Controller.Tests.Entities;

[Collection(BaseItemStaticStateFixture.Name)]
public class FolderTests
{
    private static (Mock<IChannelManager> ChannelManager, Mock<ICollectionManager> CollectionManager, Mock<IUserViewManager> UserViewManager, Mock<ITVSeriesManager> TvSeriesManager) StrictManagers()
        => (new Mock<IChannelManager>(MockBehavior.Strict),
            new Mock<ICollectionManager>(MockBehavior.Strict),
            new Mock<IUserViewManager>(MockBehavior.Strict),
            new Mock<ITVSeriesManager>(MockBehavior.Strict));

    [Fact]
    public void GetItems_NonChannelFolder_NoUser_ReturnsChildrenWithoutTouchingOtherManagers()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        var child1 = new Movie { Id = Guid.NewGuid() };
        var child2 = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child1, child2 };

        BaseItem.LibraryManager = new Mock<ILibraryManager>().Object;
        BaseItem.UserDataManager = new Mock<IUserDataManager>().Object;

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();

        var result = folder.GetItems(new InternalItemsQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(child1, result.Items);
        Assert.Contains(child2, result.Items);

        // Plain library folder, no user, no BoxSet collapsing: none of the 4 threaded
        // dependencies (replacing the removed BaseItem/Folder statics) should be touched.
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetItems_ChannelFolder_DelegatesToChannelManagerOnly()
    {
        var folder = new Folder { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid() };
        var channelItem = new Movie { Id = Guid.NewGuid() };

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();
        channelManager
            .Setup(x => x.GetChannelItemsInternal(It.IsAny<InternalItemsQuery>(), It.IsAny<IProgress<double>>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new Reefin.Model.Querying.QueryResult<BaseItem>(0, 1, new BaseItem[] { channelItem }));

        var result = folder.GetItems(new InternalItemsQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);

        Assert.Single(result.Items);
        Assert.Equal(channelItem, result.Items[0]);
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetItemList_NonChannelFolder_NoUser_ReturnsChildrenAsFlatList()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };

        BaseItem.LibraryManager = new Mock<ILibraryManager>().Object;
        BaseItem.UserDataManager = new Mock<IUserDataManager>().Object;

        var (channelManager, collectionManager, userViewManager, tvSeriesManager) = StrictManagers();

        var result = folder.GetItemList(new InternalItemsQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);

        Assert.Single(result);
        Assert.Equal(child, result[0]);
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }
}
