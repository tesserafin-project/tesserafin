using System;
using Moq;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.Entities.TV;
using Reefin.Controller.Library;
using Reefin.Controller.Playlists;
using Reefin.Controller.Sorting;
using Reefin.Database.Implementations.Entities;
using Reefin.Model.Library;
using Reefin.Model.Querying;
using Reefin.Server.Core.Library;
using Xunit;

namespace Reefin.Server.Implementations.Tests.Library;

/// <summary>
/// Behavioral parity tests for <see cref="ItemQueryScopeService"/>, mirroring the characterization
/// tests in <c>LibraryManagerGlobalQueryTests</c> (PR85) that pinned down the equivalent
/// <c>LibraryManager</c> private helpers this service copies from
/// (<c>SetTopParentIdsOrAncestors</c>/<c>AddUserToQuery</c>/<c>GetTopParentIdsForQuery</c>). These
/// exercise the extracted service directly, with <see cref="IItemLookupService"/>,
/// <see cref="IUserViewCatalog"/>, <see cref="IItemSortService"/> and
/// <see cref="IUserRootFolderProvider"/> mocked. <b>PR110</b>: this service now depends on
/// <see cref="IUserViewCatalog"/> instead of <c>IUserViewManager</c> - it holds no direct or
/// transitive reference to <c>ILibraryManager</c>, <c>IUserViewManager</c>, <c>IChannelManager</c> or
/// <c>ILiveTvManager</c>, so it is a fully cycle-free leaf (see
/// <c>docs/pr85b-item-query-scope-service.md</c>).
/// </summary>
public class ItemQueryScopeServiceTests
{
    private readonly Mock<IItemLookupService> _itemLookupServiceMock = new();
    private readonly Mock<IUserViewCatalog> _userViewCatalogMock = new();
    private readonly Mock<IItemSortService> _itemSortServiceMock = new();
    private readonly Mock<IUserRootFolderProvider> _rootFolderProviderMock = new();
    private readonly ItemQueryScopeService _scopeService;

    public ItemQueryScopeServiceTests()
    {
        _scopeService = new ItemQueryScopeService(
            _itemLookupServiceMock.Object,
            _userViewCatalogMock.Object,
            _itemSortServiceMock.Object,
            _rootFolderProviderMock.Object);
    }

    // ---------------------------------------------------------------
    // 1. A plain Folder parent (not a collection/user view, not a Playlist/BoxSet with linked
    // children) routes through AncestorIds (LibraryManager.cs "else" branch, L1972-1979).
    // ---------------------------------------------------------------

    [Fact]
    public void SetTopParentIdsOrAncestors_PlainFolderParent_SetsAncestorIds()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Series" };
        var query = new InternalItemsQuery { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [series]);

        Assert.Equal(new[] { series.Id }, query.AncestorIds);
        Assert.Empty(query.TopParentIds);
    }

    // ---------------------------------------------------------------
    // 2. AddUserToQuery resolves the user's views into TopParentIds via IUserViewCatalog when the
    // query does not already carry any scoping (LibraryManager.cs L1985-2016).
    // ---------------------------------------------------------------

    [Fact]
    public void AddUserToQuery_UnscopedQuery_ResolvesUserViewsIntoTopParentIds()
    {
        var user = new User("test-user", "provider", "provider");
        var physicalFolderId = Guid.NewGuid();
        var userView = new CollectionFolder { Id = Guid.NewGuid(), Name = "Movies", PhysicalFolderIds = [physicalFolderId] };

        _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>())).Returns([userView]);

        var query = new InternalItemsQuery(user);

        _scopeService.AddUserToQuery(query, user);

        Assert.Equal(new[] { physicalFolderId }, query.TopParentIds);
        _userViewCatalogMock.Verify(
            m => m.GetUserViews(It.Is<UserViewQuery>(q => q.User == user && q.IncludeHidden)),
            Times.Once);
    }

    // ---------------------------------------------------------------
    // 3. Empty-scope guard: when top-parent scoping resolves to an empty set, a fresh sentinel GUID
    // is injected so the query never falls back to scanning all libraries
    // (SetTopParentIdsOrAncestors, LibraryManager.cs L1943-1949).
    // ---------------------------------------------------------------

    [Fact]
    public void SetTopParentIdsOrAncestors_ParentResolvesToEmptyScope_InjectsSentinelTopParentId()
    {
        // A CollectionFolder (ICollectionFolder) with no PhysicalFolderIds hits the "optimize by
        // querying against top level views" branch and resolves to zero top-parent ids.
        var emptyView = new CollectionFolder { Id = Guid.NewGuid(), Name = "Empty" };
        var query = new InternalItemsQuery { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [emptyView]);

        Assert.Single(query.TopParentIds);
        Assert.NotEqual(Guid.Empty, query.TopParentIds[0]);
        Assert.Empty(query.AncestorIds);
    }

    // ---------------------------------------------------------------
    // 4. A single Playlist (or BoxSet) parent whose LinkedChildren is non-empty routes via
    // query.ItemIds (the linked child ids), NOT AncestorIds (LinkedChildren branch,
    // LibraryManager.cs L1951-1969).
    // ---------------------------------------------------------------

    [Fact]
    public void SetTopParentIdsOrAncestors_PlaylistWithLinkedChildren_RoutesViaItemIds()
    {
        var child1Id = Guid.NewGuid();
        var child2Id = Guid.NewGuid();
        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = "Playlist",
            LinkedChildren =
            [
                new LinkedChild { ItemId = child1Id },
                new LinkedChild { ItemId = child2Id }
            ]
        };
        var query = new InternalItemsQuery { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [playlist]);

        Assert.Equal(new[] { child1Id, child2Id }, query.ItemIds);
        Assert.Empty(query.AncestorIds);
    }

    [Fact]
    public void SetTopParentIdsOrAncestors_BoxSetWithLinkedChildren_RoutesViaItemIds()
    {
        var childId = Guid.NewGuid();
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            Name = "BoxSet",
            LinkedChildren = [new LinkedChild { ItemId = childId }]
        };
        var query = new InternalItemsQuery { Recursive = true };

        _scopeService.SetTopParentIdsOrAncestors(query, [boxSet]);

        Assert.Equal(new[] { childId }, query.ItemIds);
        Assert.Empty(query.AncestorIds);
    }

    // ---------------------------------------------------------------
    // 5. AddUserToQuery(query, user, allowExternalContent: false) propagates
    // IncludeExternalContent == false into the UserViewQuery used to resolve user views
    // (LibraryManager.cs L1985 / L2005).
    // ---------------------------------------------------------------

    [Fact]
    public void AddUserToQuery_AllowExternalContentFalse_PropagatesIncludeExternalContentFalseToUserViewQuery()
    {
        var user = new User("test-user", "provider", "provider");
        UserViewQuery? capturedQuery = null;
        _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>()))
            .Callback<UserViewQuery>(q => capturedQuery = q)
            .Returns([]);

        var query = new InternalItemsQuery(user);

        _scopeService.AddUserToQuery(query, user, allowExternalContent: false);

        Assert.NotNull(capturedQuery);
        Assert.False(capturedQuery!.IncludeExternalContent);
    }

    // ---------------------------------------------------------------
    // 6. AddUserToQuery is a no-op when the query is already scoped (e.g. non-empty AncestorIds) -
    // IUserViewCatalog is never consulted (LibraryManager.cs L1992-1999 guard condition).
    // ---------------------------------------------------------------

    [Fact]
    public void AddUserToQuery_AlreadyScopedQuery_DoesNotResolveUserViews()
    {
        var user = new User("test-user", "provider", "provider");
        var query = new InternalItemsQuery(user) { AncestorIds = [Guid.NewGuid()] };

        _scopeService.AddUserToQuery(query, user);

        _userViewCatalogMock.Verify(m => m.GetUserViews(It.IsAny<UserViewQuery>()), Times.Never);
        Assert.Empty(query.TopParentIds);
    }

    // ---------------------------------------------------------------
    // 7. AddUserToQuery sets query.User when the query does not already have one
    // (LibraryManager.cs L1987-1990).
    // ---------------------------------------------------------------

    [Fact]
    public void AddUserToQuery_QueryWithoutUser_SetsUserOnQuery()
    {
        var user = new User("test-user", "provider", "provider");
        _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>())).Returns([]);

        var query = new InternalItemsQuery();

        _scopeService.AddUserToQuery(query, user);

        Assert.Equal(user, query.User);
    }

    // ---------------------------------------------------------------
    // 8. AddUserToQuery resolves a plain (non-view, non-collection) user view into its top parent
    // WITHOUT touching the static BaseItem.LibraryManager. The item's parent chain is resolved purely
    // through IItemLookupService; a strict BaseItem.LibraryManager mock proves no static hop occurs.
    // This pins PR90's static-free query-scoping fallback.
    // ---------------------------------------------------------------

    [Fact]
    public void AddUserToQuery_PlainViewResolvesTopParent_StaysOffStaticLibraryManager()
    {
        // Strict: any call to the static BaseItem.LibraryManager throws, failing the test.
        var previous = BaseItem.LibraryManager;
        BaseItem.LibraryManager = new Mock<ILibraryManager>(MockBehavior.Strict).Object;
        try
        {
            var user = new User("test-user", "provider", "provider");

            // Aggregate root: makes its direct child a top parent (IsTopParentVia -> GetParent(lookup) is AggregateFolder).
            var aggregate = new AggregateFolder { Id = Guid.NewGuid(), Name = "Root" };
            var topFolder = new Folder { Id = Guid.NewGuid(), Name = "Top", ParentId = aggregate.Id };
            // Leaf returned by GetUserViews: plain Folder, not top, parent is topFolder.
            var leaf = new Folder { Id = Guid.NewGuid(), Name = "Leaf", ParentId = topFolder.Id };

            _itemLookupServiceMock.Setup(m => m.GetItemById(topFolder.Id)).Returns(topFolder);
            _itemLookupServiceMock.Setup(m => m.GetItemById(aggregate.Id)).Returns(aggregate);
            _userViewCatalogMock.Setup(m => m.GetUserViews(It.IsAny<UserViewQuery>())).Returns(new[] { leaf });

            var query = new InternalItemsQuery(user);

            _scopeService.AddUserToQuery(query, user);

            // Resolved purely through the lookup mock: leaf -> topFolder (top parent).
            Assert.Equal(new[] { topFolder.Id }, query.TopParentIds);
        }
        finally
        {
            BaseItem.LibraryManager = previous;
        }
    }
}
