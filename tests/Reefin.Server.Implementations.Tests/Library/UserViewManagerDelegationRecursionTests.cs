using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Controller.Channels;
using Reefin.Controller.Collections;
using Reefin.Controller.Configuration;
using Reefin.Controller.Dto;
using Reefin.Controller.Entities;
using Reefin.Controller.Library;
using Reefin.Controller.LiveTv;
using Reefin.Controller.Persistence;
using Reefin.Controller.Providers;
using Reefin.Controller.Sorting;
using Reefin.Controller.TV;
using Reefin.Data.Enums;
using Reefin.Database.Implementations.Entities;
using Reefin.Model.IO;
using Reefin.Model.Library;
using Reefin.Model.Querying;
using Reefin.Server.Core.Library;
using Xunit;

namespace Reefin.Server.Implementations.Tests.Library;

/// <summary>
/// PR110's required anti-recursion proof (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §9/PR110):
/// once <c>UserViewManager.GetUserViews</c> delegates to <see cref="IUserViewCatalog"/>, and
/// <see cref="UserViewCatalog"/>'s playlists/boxsets probe forces <c>Lazy&lt;IItemQueryService&gt;.Value</c>
/// (whose concrete <c>ItemQueryService</c> holds <c>IUserViewManager</c> directly, non-Lazy, for its
/// own <c>Folder.GetItems</c>/<c>GetItemList</c> compatibility fallback), the object graph contains a
/// genuine reference cycle:
/// <c>UserViewManager -&gt; IUserViewCatalog -&gt; Lazy&lt;IItemQueryService&gt; -&gt; ItemQueryService
/// -&gt; IUserViewManager</c> (back to the very same instance). This test builds that exact cycle with
/// real, non-mocked production types on both ends and drives a real playlists-library
/// <c>GetUserViews</c> call through it end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this doesn't infinite-loop</b> (traced statically before writing this test, see PR110's
/// final report): <see cref="ItemQueryService.GetItemList(Folder, InternalItemsQuery)"/> only reaches
/// <c>IUserViewManager</c> on its slow-path fallback
/// (<c>CanUseRawQueryItemsFastPath</c> false). For the exact query
/// <see cref="UserViewCatalog"/>'s probe issues - a non-recursive query with no <c>ItemIds</c>, against
/// a plain top-level <c>CollectionFolder</c> whose <c>SupportsRawQueryItems</c> defaults to
/// <c>true</c> (unlike <c>UserView</c>/<c>BoxSet</c>/<c>Season</c>/<c>Series</c>, which override it to
/// <c>false</c>) - the fast path is always taken instead:
/// <c>GetRawQueryItems</c> + <c>PostFilterAndSort</c>, which never touches <c>IUserViewManager</c>.
/// </para>
/// <para>
/// <b>How a regression would show up</b>: a real recursive cycle would eventually
/// <see cref="StackOverflowException"/>, which is uncatchable and kills the test runner rather than
/// failing this test cleanly. To get a clean, catchable failure instead, the <c>IUserViewManager</c>
/// wired into <c>ItemQueryService</c> is <see cref="ReentrancyGuardUserViewManager"/> - a decorator
/// around the *same* real <see cref="Reefin.Server.Core.Library.UserViewManager"/> instance under
/// test, sharing one re-entrancy counter across both the outer test call and any call
/// <c>ItemQueryService</c> might (incorrectly) make back into it. A second, nested entry throws a
/// plain <see cref="InvalidOperationException"/> instead of recursing further.
/// </para>
/// </remarks>
public sealed class UserViewManagerDelegationRecursionTests
{
    [Fact]
    public void GetUserViews_PlaylistsLibrary_FullDelegationChain_DoesNotRecurse()
    {
        var internalMetadataPath = Path.Combine(Path.GetTempPath(), "reefin-uvm-recursion-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(internalMetadataPath);

        var configMock = new Mock<IServerConfigurationManager>();
        configMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        configMock.Setup(c => c.ApplicationPaths.InternalMetadataPath).Returns(internalMetadataPath);
        configMock.Setup(c => c.Configuration).Returns(new Reefin.Model.Configuration.ServerConfiguration());
        BaseItem.ConfigurationManager = configMock.Object;
        BaseItem.LibraryManager = Mock.Of<ILibraryManager>();

        var localizationMock = new Mock<Reefin.Model.Globalization.ILocalizationManager>();

        var itemSortService = new Reefin.Server.Core.Sorting.ItemSortService(Mock.Of<IUserManager>(), Mock.Of<IUserDataManager>());

        // Real IUserViewFactory, built on real ItemLookupService/ItemStore (mirrors
        // UserViewCatalogParityTests' Fixture) - not exercised by this test's playlists probe, but
        // needed because UserView.IsUserSpecific(playlists folder) routes through it.
        var itemRepositoryMock = new Mock<IItemRepository>();
        itemRepositoryMock.Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null);
        var persistenceServiceMock = new Mock<IItemPersistenceService>();
        var itemLookupService = new Reefin.Server.Core.Library.ItemLookupService(itemRepositoryMock.Object, configMock.Object);
        var itemStore = new Reefin.Server.Core.Library.ItemStore(
            persistenceServiceMock.Object,
            itemLookupService,
            configMock.Object,
            NullLogger<Reefin.Server.Core.Library.ItemStore>.Instance);
        var fileSystemMock = new Mock<IFileSystem>();
        fileSystemMock.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns<string>(s => s);
        var userViewFactory = new Reefin.Server.Core.Library.UserViewFactory(
            itemLookupService,
            itemStore,
            configMock.Object,
            fileSystemMock.Object,
            new Lazy<IProviderManager>(() => Mock.Of<IProviderManager>()));

        var rootProviderMock = new Mock<IUserRootFolderProvider>();
        var probeItem = new ProbeVideo { Id = Guid.NewGuid(), Name = "probe-item", Visible = true };
        var playlists = new ProbeFolder(probeItem) { Id = Guid.NewGuid(), Name = "Playlists", CollectionType = CollectionType.playlists };
        rootProviderMock.Setup(p => p.GetUserRootFolder()).Returns(new FixtureRootFolder(playlists));

        var channelCatalogMock = new Mock<IChannelCatalog>();
        var liveTvPresenceMock = new Mock<ILiveTvPresenceProvider>();

        // The reference cycle under test: `manager` (assigned below, after construction) is captured
        // by this closure. By the time .Value is forced (deep inside manager.GetUserViews, well after
        // `manager` is assigned), the guard wraps the fully-constructed real UserViewManager.
        ReentrancyGuardUserViewManager? guard = null;
        var itemQueryServiceFactory = new Lazy<IItemQueryService>(() => new Reefin.Server.Core.Library.ItemQueryService(
            Mock.Of<IChannelManager>(),
            Mock.Of<ICollectionManager>(),
            guard!,
            Mock.Of<ITVSeriesManager>(),
            configMock.Object,
            itemSortService,
            Mock.Of<IItemLookupService>(),
            Mock.Of<IItemRepository>(),
            Mock.Of<IItemQueryScopeService>()));

        var catalog = new UserViewCatalog(
            rootProviderMock.Object,
            userViewFactory,
            channelCatalogMock.Object,
            liveTvPresenceMock.Object,
            itemSortService,
            configMock.Object,
            localizationMock.Object,
            itemQueryServiceFactory);

        var libraryManagerMock = new Mock<ILibraryManager>();
        var channelManagerMock = new Mock<IChannelManager>();

        var realManager = new Reefin.Server.Core.Library.UserViewManager(
            libraryManagerMock.Object,
            localizationMock.Object,
            channelManagerMock.Object,
            itemSortService,
            catalog,
            userViewFactory);

        guard = new ReentrancyGuardUserViewManager(realManager);

        var user = new User("recursion-test-user", "auth", "reset") { Id = Guid.NewGuid() };

        // If any of this recurses, either a StackOverflowException kills the runner (uncatchable) or
        // ReentrancyGuardUserViewManager throws InvalidOperationException on the second entry - either
        // way, this assertion is never reached with a green result.
        var result = guard.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = false });

        // Playlists are IsUserSpecific -> always a per-user named view, proving the call actually ran
        // the full delegation chain (not merely returned early) before terminating cleanly.
        var view = Assert.Single(result);
        Assert.IsType<UserView>(view);
        Assert.Equal(CollectionType.playlists, ((UserView)view).ViewType);
    }

    /// <summary>
    /// Decorator around a real <see cref="IUserViewManager"/> that throws a plain, catchable
    /// <see cref="InvalidOperationException"/> on re-entrant calls instead of letting a real cycle
    /// recurse until <see cref="StackOverflowException"/> (uncatchable, kills the test runner).
    /// </summary>
    private sealed class ReentrancyGuardUserViewManager : IUserViewManager
    {
        private readonly IUserViewManager _inner;
        private int _depth;

        public ReentrancyGuardUserViewManager(IUserViewManager inner)
        {
            _inner = inner;
        }

        public Folder[] GetUserViews(UserViewQuery query) => Guard(() => _inner.GetUserViews(query));

        public UserView GetUserSubView(Guid parentId, CollectionType? type, string localizationKey, string sortName)
            => Guard(() => _inner.GetUserSubView(parentId, type, localizationKey, sortName));

        public List<Tuple<BaseItem, List<BaseItem>>> GetLatestItems(LatestItemsQuery request, DtoOptions options)
            => Guard(() => _inner.GetLatestItems(request, options));

        private T Guard<T>(Func<T> call)
        {
            if (Interlocked.Increment(ref _depth) > 1)
            {
                throw new InvalidOperationException(
                    "Reentrant call into IUserViewManager detected while already inside GetUserViews - " +
                    "PR110's UserViewManager -> IUserViewCatalog -> Lazy<IItemQueryService> -> " +
                    "ItemQueryService -> IUserViewManager cycle recursed instead of terminating on the " +
                    "raw-query fast path.");
            }

            try
            {
                return call();
            }
            finally
            {
                Interlocked.Decrement(ref _depth);
            }
        }
    }

    /// <summary>
    /// Root folder double whose <see cref="GetChildren(User, bool, InternalItemsQuery, IItemSortService)"/>
    /// returns a fixed set of children regardless of arguments (mirrors
    /// <c>UserViewCatalogParityTests.FixtureRootFolder</c>/<c>UserViewCatalogTests.FakeChildrenFolder</c>).
    /// </summary>
    private sealed class FixtureRootFolder : Folder
    {
        private readonly IReadOnlyList<BaseItem> _children;

        public FixtureRootFolder(params Folder[] children)
        {
            _children = children;
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query, IItemSortService itemSortService)
        {
            return _children;
        }
    }

    /// <summary>
    /// Playlists library folder double whose <see cref="GetRawQueryItems(InternalItemsQuery)"/> is
    /// overridden to return a fixed item set (mirrors <c>UserViewCatalogParityTests.ProbeFolder</c>) -
    /// a plain <see cref="CollectionFolder"/> subclass, so <see cref="Folder.SupportsRawQueryItems"/>
    /// keeps its base-class default of <c>true</c>, driving <c>ItemQueryService</c>'s raw-query fast
    /// path (the path that never touches <c>IUserViewManager</c>).
    /// </summary>
    private sealed class ProbeFolder : CollectionFolder
    {
        private readonly IReadOnlyList<BaseItem> _items;

        public ProbeFolder(params BaseItem[] items)
        {
            _items = items;
        }

        public override IEnumerable<BaseItem> GetRawQueryItems(InternalItemsQuery query) => _items;
    }

    /// <summary>
    /// Minimal <see cref="Video"/> double with a directly-controlled <see cref="IsVisible"/> result -
    /// only the boolean matters for the playlist/boxset probe's visibility check.
    /// </summary>
    private sealed class ProbeVideo : Video
    {
        public bool Visible { get; init; }

        public override bool IsVisible(User user, bool skipAllowedTagsCheck = false) => Visible;
    }
}
