using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Controller.Channels;
using Reefin.Controller.Collections;
using Reefin.Controller.Configuration;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Library;
using Reefin.Controller.Sorting;
using Reefin.Controller.TV;
using Reefin.Data.Enums;
using Reefin.Database.Implementations.Entities;
using Reefin.Database.Implementations.Enums;
using Reefin.Model.Configuration;
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
        out Mock<ITVSeriesManager> tvSeriesManager,
        ILibraryManager? libraryManager = null,
        IServerConfigurationManager? configurationManager = null)
    {
        channelManager = new Mock<IChannelManager>();
        collectionManager = new Mock<ICollectionManager>();
        userViewManager = new Mock<IUserViewManager>();
        tvSeriesManager = new Mock<ITVSeriesManager>();

        libraryManager ??= new Mock<ILibraryManager>().Object;
        configurationManager ??= Mock.Of<IServerConfigurationManager>(x => x.Configuration == new ServerConfiguration());

        BaseItem.LibraryManager = libraryManager;
        BaseItem.ConfigurationManager = configurationManager;
        BaseItem.UserDataManager = new Mock<IUserDataManager>().Object;

        return new ItemQueryService(channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object, libraryManager, configurationManager, new PassthroughItemSortService());
    }

    [Fact]
    public void GetItems_DelegatesToFolderWithTheServiceOwnedManagers()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };

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

        var result = service.GetItemList(folder, new InternalItemsQuery());

        Assert.Single(result);
        Assert.Equal(child, result[0]);
        channelManager.VerifyNoOtherCalls();
        collectionManager.VerifyNoOtherCalls();
        userViewManager.VerifyNoOtherCalls();
        tvSeriesManager.VerifyNoOtherCalls();
    }

    // Parity tests (major rewrite plan, PR23/N): ItemQueryService.PostFilterAndSort is a relocation
    // of Folder.PostFilterAndSort - not wired into GetItems/GetItemList yet (that's PR24, once a
    // raw-children primitive exists on Folder). These compare the new standalone method against the
    // still-untouched Folder.GetItems (which still runs its own copy of the same logic) given the
    // same raw items, to prove behavior was reproduced exactly, not just relocated by inspection.

    [Fact]
    public void PostFilterAndSort_UserNull_MatchesFolderGetItems()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child1 = new Movie { Id = Guid.NewGuid(), Name = "Alpha" };
        var child2 = new Movie { Id = Guid.NewGuid(), Name = "Beta" };
        folder.Children = new BaseItem[] { child1, child2 };

        // Parity test deliberately compares against the obsolete 5-parameter overload — see
        // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
        var expected = folder.GetItems(new InternalItemsQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);
#pragma warning restore CS0618
        var actual = service.PostFilterAndSort(folder, folder.Children, new InternalItemsQuery());

        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.TotalRecordCount, actual.TotalRecordCount);
    }

    [Fact]
    public void PostFilterAndSort_UserNonNull_CollapseFalse_MatchesFolderGetItems()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid(), Name = "Alpha" };
        folder.Children = new BaseItem[] { child };
        var user = new User("test-user", "provider", "provider");

        InternalItemsQuery MakeQuery() => new() { User = user, CollapseBoxSetItems = false };

        // Parity test deliberately compares against the obsolete 5-parameter overload — see
        // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
        var expected = folder.GetItems(MakeQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);
#pragma warning restore CS0618
        var actual = service.PostFilterAndSort(folder, folder.Children, MakeQuery());

        Assert.Equal(expected.Items, actual.Items);
        collectionManager.Verify(x => x.CollapseItemsWithinBoxSets(It.IsAny<IEnumerable<BaseItem>>(), It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public void PostFilterAndSort_UserNonNull_CollapseTrue_MatchesFolderGetItems()
    {
        var configurationManager = Mock.Of<IServerConfigurationManager>(x => x.Configuration == new ServerConfiguration
        {
            EnableGroupingMoviesIntoCollections = true,
            EnableGroupingShowsIntoCollections = true,
        });
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, configurationManager: configurationManager);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid(), Name = "Alpha" };
        folder.Children = new BaseItem[] { child };
        var user = new User("test-user", "provider", "provider");

        collectionManager
            .Setup(x => x.CollapseItemsWithinBoxSets(It.IsAny<IEnumerable<BaseItem>>(), user))
            .Returns((IEnumerable<BaseItem> items, User _) => items);

        InternalItemsQuery MakeQuery() => new() { User = user, CollapseBoxSetItems = true };

        // Parity test deliberately compares against the obsolete 5-parameter overload — see
        // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
        var expected = folder.GetItems(MakeQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);
#pragma warning restore CS0618
        var actual = service.PostFilterAndSort(folder, folder.Children, MakeQuery());

        Assert.Equal(expected.Items, actual.Items);
        // Both the untouched Folder path and the new relocated path call the same collectionManager
        // instance once each with the exact same arguments - not just "similar output".
        collectionManager.Verify(x => x.CollapseItemsWithinBoxSets(It.IsAny<IEnumerable<BaseItem>>(), user), Times.Exactly(2));
    }

    [Theory]
    [InlineData("B", "", "", "Beta")]
    [InlineData("", "B", "", "Beta")]
    [InlineData("", "", "B", "Alpha")]
    public void PostFilterAndSort_NameFiltersAfterCollapse_MatchesFolderGetItems(string nameStartsWith, string nameStartsWithOrGreater, string nameLessThan, string expectedName)
    {
        var configurationManager = Mock.Of<IServerConfigurationManager>(x => x.Configuration == new ServerConfiguration
        {
            EnableGroupingMoviesIntoCollections = true,
            EnableGroupingShowsIntoCollections = true,
        });
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, configurationManager: configurationManager);

        var folder = new Folder { Id = Guid.NewGuid() };
        var alpha = new Movie { Id = Guid.NewGuid(), Name = "Alpha" };
        var beta = new Movie { Id = Guid.NewGuid(), Name = "Beta" };
        folder.Children = new BaseItem[] { alpha, beta };
        var user = new User("test-user", "provider", "provider");

        collectionManager
            .Setup(x => x.CollapseItemsWithinBoxSets(It.IsAny<IEnumerable<BaseItem>>(), user))
            .Returns((IEnumerable<BaseItem> items, User _) => items);

        InternalItemsQuery MakeQuery() => new()
        {
            User = user,
            CollapseBoxSetItems = true,
            NameStartsWith = nameStartsWith,
            NameStartsWithOrGreater = nameStartsWithOrGreater,
            NameLessThan = nameLessThan,
        };

        // Parity test deliberately compares against the obsolete 5-parameter overload — see
        // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
        var expected = folder.GetItems(MakeQuery(), channelManager.Object, collectionManager.Object, userViewManager.Object, tvSeriesManager.Object);
#pragma warning restore CS0618
        var actual = service.PostFilterAndSort(folder, folder.Children, MakeQuery());

        Assert.Equal(expected.Items.Select(i => i.Name), actual.Items.Select(i => i.Name));
        Assert.Single(actual.Items);
        Assert.Equal(expectedName, actual.Items[0].Name);
    }

    // Wiring tests (major rewrite plan, PR25/N): GetItems/GetItemList now take a fast path
    // (GetRawQueryItems + PostFilterAndSort) for folders where Folder.SupportsRawQueryItems is
    // true. For the safe case the fast path is behaviorally equivalent to the old folder.GetItems
    // call by construction (Folder.GetItemsInternal routes through the same GetRawQueryItems,
    // PR24) - not worth re-proving here. What IS worth proving: the 4 cases where taking the fast
    // path would be a silent correctness bug (SupportsRawQueryItems false, Recursive, ItemIds,
    // channel source) actually fall back to the old, still-correct path instead.

    [Fact]
    public void GetItems_RecursiveQuery_FallsBackInsteadOfRawQueryItems()
    {
        var sentinel = new Movie { Id = Guid.NewGuid() };
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(0, 1, new BaseItem[] { sentinel }));
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, libraryManager: libraryManager.Object);

        var folder = new Folder { Id = Guid.NewGuid() };
        // Present in Children so a wrongly-taken fast path would return this instead of the sentinel below.
        folder.Children = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        var result = service.GetItems(folder, new InternalItemsQuery { Recursive = true });

        Assert.Single(result.Items);
        Assert.Equal(sentinel, result.Items[0]);
    }

    [Fact]
    public void GetItems_ItemIdsQuery_FallsBackInsteadOfRawQueryItems()
    {
        var sentinel = new Movie { Id = Guid.NewGuid() };
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(0, 1, new BaseItem[] { sentinel }));
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, libraryManager: libraryManager.Object);

        var folder = new Folder { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        var result = service.GetItems(folder, new InternalItemsQuery { ItemIds = new[] { sentinel.Id } });

        Assert.Single(result.Items);
        Assert.Equal(sentinel, result.Items[0]);
    }

    [Fact]
    public void GetItems_ChannelFolder_FallsBackInsteadOfRawQueryItems()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager);

        var folder = new Folder { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid() };
        folder.Children = new BaseItem[] { new Movie { Id = Guid.NewGuid() } };

        var channelItem = new Movie { Id = Guid.NewGuid() };
        channelManager
            .Setup(x => x.GetChannelItemsInternal(It.IsAny<InternalItemsQuery>(), It.IsAny<IProgress<double>>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new QueryResult<BaseItem>(0, 1, new BaseItem[] { channelItem }));

        var result = service.GetItems(folder, new InternalItemsQuery());

        Assert.Single(result.Items);
        Assert.Equal(channelItem, result.Items[0]);
    }

    [Fact]
    public void GetItems_UserView_FallsBackInsteadOfRawQueryItems()
    {
        // UserView.SupportsRawQueryItems is false - its own GetItemsInternal (resolving
        // DisplayParentId to a channel-sourced Folder, cf. FolderTests' equivalent test) must be
        // used, not the base GetRawQueryItems on the UserView itself (which has no Children set here
        // and would silently return an empty result if the fast path were wrongly taken).
        var displayParent = new Folder { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid() };
        var userView = new UserView { Id = Guid.NewGuid(), DisplayParentId = displayParent.Id };
        var channelItem = new Movie { Id = Guid.NewGuid() };

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetItemById(displayParent.Id)).Returns(displayParent);
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager, libraryManager: libraryManager.Object);
        BaseItem.Logger = NullLogger<BaseItem>.Instance;

        channelManager
            .Setup(x => x.GetChannelItemsInternal(It.IsAny<InternalItemsQuery>(), It.IsAny<IProgress<double>>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new QueryResult<BaseItem>(0, 1, new BaseItem[] { channelItem }));

        var result = service.GetItems(userView, new InternalItemsQuery());

        Assert.Single(result.Items);
        Assert.Equal(channelItem, result.Items[0]);
    }

    [Fact]
    public void GetItemList_FastPath_ForcesEnableTotalRecordCountFalse()
    {
        var service = CreateService(out var channelManager, out var collectionManager, out var userViewManager, out var tvSeriesManager);

        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        folder.Children = new BaseItem[] { child };

        var query = new InternalItemsQuery { EnableTotalRecordCount = true };

        var result = service.GetItemList(folder, query);

        Assert.Single(result);
        Assert.Equal(child, result[0]);
        Assert.False(query.EnableTotalRecordCount);
    }

    private sealed class PassthroughItemSortService : IItemSortService
    {
        public void AddParts(IEnumerable<IBaseItemComparer> itemComparers)
        {
        }

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder)
            => items;

        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy)
            => items;
    }
}
