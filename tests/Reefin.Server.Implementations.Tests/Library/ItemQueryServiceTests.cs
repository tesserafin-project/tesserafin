using System;
using Moq;
using Reefin.Controller.Channels;
using Reefin.Controller.Collections;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Library;
using Reefin.Controller.TV;
using Reefin.Model.Querying;
using Reefin.Server.Core.Library;
using Xunit;

namespace Reefin.Server.Implementations.Tests.Library;

public class ItemQueryServiceTests
{
    private static ItemQueryService CreateService(
        out Mock<IChannelManager> channelManager,
        out Mock<ICollectionManager> collectionManager,
        out Mock<IUserViewManager> userViewManager,
        out Mock<ITVSeriesManager> tvSeriesManager)
    {
        channelManager = new Mock<IChannelManager>();
        collectionManager = new Mock<ICollectionManager>();
        userViewManager = new Mock<IUserViewManager>();
        tvSeriesManager = new Mock<ITVSeriesManager>();

        return new ItemQueryService(channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);
    }

    [Fact]
    public void GetItems_DelegatesToFolderWithTheServiceOwnedManagers()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };
        BaseItem.LibraryManager = new Mock<ILibraryManager>().Object;
        BaseItem.UserDataManager = new Mock<IUserDataManager>().Object;

        var result = service.GetItems(folder, new InternalItemsQuery());

        Assert.Single(result.Items);
        Assert.Equal(child, result.Items[0]);

        // Not a real assertion on channelManager/collectionManager/userViewManager/tvSeriesManager
        // being called (this plain library folder doesn't touch them, same as FolderTests) - the
        // point of this test is that the service compiles/wires the 4 dependencies through to
        // Folder.GetItems without the caller having to hold them itself.
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetItemList_DelegatesToFolderWithTheServiceOwnedManagers()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };
        BaseItem.LibraryManager = new Mock<ILibraryManager>().Object;
        BaseItem.UserDataManager = new Mock<IUserDataManager>().Object;

        var result = service.GetItemList(folder, new InternalItemsQuery());

        Assert.Single(result);
        Assert.Equal(child, result[0]);
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }
}
