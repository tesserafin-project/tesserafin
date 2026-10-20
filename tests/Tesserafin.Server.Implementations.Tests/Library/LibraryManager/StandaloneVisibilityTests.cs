using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations.Entities;
using Tesserafin.Database.Implementations.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Serialization;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Characterisation of the standalone visibility check behind access by id
/// (<c>IsVisibleStandaloneInternal</c>): the item's own visibility, every parent's (allowed tags
/// skipped), then the library restriction - blocked libraries when the user has any, otherwise all
/// libraries or the enabled ones - applied to the libraries the item belongs to, unless the top parent
/// has no path or the item belongs to no library. Every expectation is hand-derived and was green on
/// the unmodified entities, read through the static <see cref="BaseItem.LibraryManager"/>; each
/// decision is taken both through <see cref="IItemAccessService.GetVisibleItemById{T}"/> and through
/// the static-backed <see cref="BaseItem.IsVisibleStandalone(User)"/>, and the two must agree.
/// </summary>
/// <remarks>
/// The item graph is real entities over a real <c>ItemLookupService</c>; only the repository
/// (id to item), the user root folder (its library children) and the library options file are stubbed.
/// </remarks>
[Collection(LibraryManagerStaticStateFixture.Name)]
public sealed class StandaloneVisibilityTests
{
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly List<Folder> _libraries = [];
    private readonly Dictionary<string, LibraryOptions> _options = [];
    private readonly IItemAccessService _access;

    public StandaloneVisibilityTests()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());

        var configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        configuration.Setup(c => c.ApplicationPaths.DataPath).Returns("/config/data");

        var repository = fixture.Freeze<Mock<IItemRepository>>();
        repository.Setup(r => r.RetrieveItem(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id)!);

        var lookup = new Tesserafin.Server.Core.Library.ItemLookupService(repository.Object, configuration.Object);
        fixture.Register<IItemLookupService>(() => lookup);
        fixture.Register<Tesserafin.Server.Core.Library.IItemCacheStore>(() => lookup);

        var root = new Folder { Id = Guid.NewGuid(), Children = _libraries };
        var rootProvider = Mock.Of<IUserRootFolderProvider>(p => p.GetUserRootFolder() == root);
        fixture.Register(() => rootProvider);

        var resolver = new Tesserafin.Server.Core.Library.CollectionFolderResolver(lookup, rootProvider);
        fixture.Register<ICollectionFolderResolver>(() => resolver);

        var manager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                Array.Empty<IResolverIgnoreRule>(),
                Array.Empty<IItemResolver>(),
                Array.Empty<IIntroProvider>(),
                fixture.Create<IEnumerable<IBaseItemComparer>>(),
                Array.Empty<ILibraryPostScanTask>()))
            .Create();

        _access = new Tesserafin.Server.Core.Library.ItemAccessService(lookup, new Tesserafin.Server.Core.Library.TagVisibilityService(lookup, resolver));

        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(LibraryOptions), It.IsAny<string>()))
            .Returns((Type _, string file) => _options.GetValueOrDefault(file)!);
        CollectionFolder.XmlSerializer = xml.Object;

        BaseItem.LibraryManager = manager;
        BaseItem.ConfigurationManager = configuration.Object;
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>(m => m.GetPathProtocol(It.IsAny<string>()) == MediaProtocol.File);
        Video.RecordingsManager = Mock.Of<IRecordingsManager>();

        var fileSystem = new Mock<Tesserafin.Model.IO.IFileSystem>();
        fileSystem.Setup(f => f.ContainsSubPath(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string parent, string path) => path.StartsWith(parent + "/", StringComparison.Ordinal));
        BaseItem.FileSystem = fileSystem.Object;
    }

    [Fact]
    public void EnableAllFolders_NoLists_AdmitsEveryLibrary()
    {
        var movie = MovieIn(AddLibrary("Movies", "/media/movies"));

        AssertVisible(movie, User(all: true), true);
    }

    [Fact]
    public void EmptyEnabledFolders_WithoutEnableAllFolders_AdmitsNoLibrary()
    {
        var movie = MovieIn(AddLibrary("Movies", "/media/movies"));

        AssertVisible(movie, User(), false);
    }

    [Fact]
    public void EnabledFolders_AdmitOnlyTheirLibraries()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var shows = AddLibrary("Shows", "/media/shows");
        var movie = MovieIn(movies);

        AssertVisible(movie, User(enabled: [movies.Id]), true);
        AssertVisible(movie, User(enabled: [shows.Id]), false);
    }

    [Fact]
    public void BlockedFolders_ExcludeTheirLibraries()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var shows = AddLibrary("Shows", "/media/shows");
        var movie = MovieIn(movies);

        AssertVisible(movie, User(blocked: [movies.Id]), false);
        AssertVisible(movie, User(blocked: [shows.Id]), true);
    }

    [Fact]
    public void NonEmptyBlockedFolders_SelectTheirOwnBranch_EnabledFoldersAreIgnored()
    {
        // Not two cumulative filters: with any blocked library, the enabled list is never read.
        var movies = AddLibrary("Movies", "/media/movies");
        var shows = AddLibrary("Shows", "/media/shows");
        var movie = MovieIn(movies);

        AssertVisible(movie, User(blocked: [shows.Id], enabled: [shows.Id]), true);
        AssertVisible(movie, User(blocked: [shows.Id]), true);
    }

    [Fact]
    public void NonEmptyBlockedFolders_SelectTheirOwnBranch_EnableAllFoldersIsIgnored()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var movie = MovieIn(movies);

        AssertVisible(movie, User(all: true, blocked: [movies.Id]), false);
        AssertVisible(movie, User(all: true, blocked: [movies.Id], enabled: [movies.Id]), false);
    }

    [Fact]
    public void BlockedFolders_AnUnknownIdOnly_AdmitsEveryLibrary()
    {
        var movie = MovieIn(AddLibrary("Movies", "/media/movies"));

        AssertVisible(movie, User(blocked: [Guid.NewGuid()]), true);
    }

    [Fact]
    public void SeveralMemberLibraries_OneAdmittedIsEnough()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var vault = AddLibrary("Vault", "/media/vault", "/media/movies");
        var movie = MovieIn(movies);

        AssertVisible(movie, User(enabled: [vault.Id]), true);
        AssertVisible(movie, User(enabled: [movies.Id]), true);
        AssertVisible(movie, User(blocked: [vault.Id]), true);
        AssertVisible(movie, User(blocked: [movies.Id, vault.Id]), false);
    }

    [Fact]
    public void ItemOutsideEveryLibrary_IsNotFolderRestricted()
    {
        AddLibrary("Shows", "/media/shows");
        var movie = AddMovie(Add(new Folder { Path = "/media/other" }), "/media/other/a.mkv");

        AssertVisible(movie, User(), true);
        AssertVisible(movie, User(blocked: [Guid.NewGuid()]), true);
    }

    [Fact]
    public void TopParentWithoutPath_SkipsTheLibraryRestriction()
    {
        // The membership walk stops below the aggregate root and matches the folder's path, but the
        // restriction looks at the topmost parent - the aggregate root - and skips itself when that has
        // no path.
        AddLibrary("Movies", "/media/movies");
        var withoutPath = Add(new AggregateFolder());
        var movie = AddMovie(Add(new Folder { Path = "/media/movies", ParentId = withoutPath.Id }), "/media/movies/a.mkv");

        AssertVisible(movie, User(), true);

        withoutPath.Path = "/config/root";
        AssertVisible(movie, User(), false);
    }

    [Fact]
    public void ParentlessItemWithoutPath_SkipsTheLibraryRestriction()
    {
        AddLibrary("Movies", "/media/movies");
        var movie = Add(new Movie());

        AssertVisible(movie, User(), true);
    }

    [Fact]
    public void LibraryRequestedDirectly_FollowsItsOwnEnabledAndBlockedState()
    {
        var root = Add(new AggregateFolder { Path = "/config/root" });
        var movies = AddLibrary("Movies", "/media/movies");
        var shows = AddLibrary("Shows", "/media/shows");
        movies.ParentId = root.Id;

        AssertVisible(movies, User(all: true), true);
        AssertVisible(movies, User(enabled: [movies.Id]), true);
        AssertVisible(movies, User(enabled: [shows.Id]), false);
        AssertVisible(movies, User(blocked: [movies.Id]), false);
        AssertVisible(movies, User(blocked: [shows.Id], enabled: [shows.Id]), true);
    }

    [Fact]
    public void DisabledLibraryRequestedDirectly_IsHidden()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        _options[System.IO.Path.Combine(movies.Path, "options.xml")] = new LibraryOptions { Enabled = false };

        AssertVisible(movies, User(all: true), false);
    }

    [Fact]
    public void ForbiddenParent_HidesTheItem_EvenWhenItsLibraryAdmitsIt()
    {
        // The parent is a blocked library folder that is not the item's library: only the parent check
        // can hide the item.
        var movies = AddLibrary("Movies", "/media/movies");
        var top = Add(new Folder { Path = "/media/movies" });
        var blockedParent = AddLibrary("Blocked", "/media/elsewhere");
        blockedParent.ParentId = top.Id;
        var movie = AddMovie(blockedParent, "/media/movies/a.mkv");

        AssertVisible(movie, User(blocked: [blockedParent.Id]), false);
        AssertVisible(movie, User(enabled: [movies.Id]), false);
        AssertVisible(movie, User(enabled: [movies.Id, blockedParent.Id]), true);
    }

    [Fact]
    public void DisabledLibraryAsParent_HidesTheItem()
    {
        AddLibrary("Movies", "/media/movies");
        var top = Add(new Folder { Path = "/media/movies" });
        var disabled = AddLibrary("Disabled", "/media/elsewhere");
        disabled.ParentId = top.Id;
        _options[System.IO.Path.Combine(disabled.Path, "options.xml")] = new LibraryOptions { Enabled = false };
        var movie = AddMovie(disabled, "/media/movies/a.mkv");

        AssertVisible(movie, User(all: true), false);
    }

    [Fact]
    public void ParentsAreCheckedWithAllowedTagsSkipped_ButBlockedTagsApply()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var top = Add(new Folder { Path = "/media/movies", Tags = ["saga"] });
        var movie = AddMovie(top, "/media/movies/a.mkv", "family");

        AssertVisible(movie, User(all: true, allowedTags: ["family"]), true);
        AssertVisible(movie, User(all: true, blockedTags: ["saga"]), false);
        AssertVisible(movie, User(enabled: [movies.Id], allowedTags: ["other"]), false);
    }

    [Fact]
    public void OwnedExtra_TakesItsOwnersLibraries()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var shows = AddLibrary("Shows", "/media/shows");
        var movie = MovieIn(movies);
        var extra = Add(new Movie { Path = "/media/movies/a-trailer.mkv", OwnerId = movie.Id });

        AssertVisible(extra, User(enabled: [movies.Id]), true);
        AssertVisible(extra, User(enabled: [shows.Id]), false);
        AssertVisible(extra, User(blocked: [movies.Id]), false);
    }

    [Fact]
    public void OwnedExtra_ItsOwnersParentsAreNotChecked()
    {
        // The extra has no parent of its own; its owner's forbidden parent does not reach it.
        AddLibrary("Movies", "/media/movies");
        var top = Add(new Folder { Path = "/media/movies" });
        var blockedParent = AddLibrary("Blocked", "/media/elsewhere");
        blockedParent.ParentId = top.Id;
        var movie = AddMovie(blockedParent, "/media/movies/a.mkv");
        var extra = Add(new Movie { Path = "/media/movies/a-trailer.mkv", OwnerId = movie.Id });

        AssertVisible(movie, User(blocked: [blockedParent.Id]), false);
        AssertVisible(extra, User(blocked: [blockedParent.Id]), true);
    }

    [Fact]
    public void UserRootFolder_IsAlwaysVisibleThroughAccess()
    {
        var root = Add(new UserRootFolder { Path = "/config/root", Tags = ["blocked"] });

        Assert.Same(root, _access.GetVisibleItemById<BaseItem>(root.Id, User(blockedTags: ["blocked"])));
    }

    [Fact]
    public void ChildOfARootFolder_SkipsAllowedTags_ButNotTheLibraryRestriction()
    {
        var root = Add(new AggregateFolder { Path = "/config/root" });
        var movies = AddLibrary("Movies", "/media/movies");
        movies.ParentId = root.Id;

        AssertVisible(movies, User(all: true, allowedTags: ["family"]), true);
        AssertVisible(movies, User(enabled: [Guid.NewGuid()], allowedTags: ["family"]), false);
    }

    [Fact]
    public void BoxSet_WithoutLinkedChildrenNorPath_KeepsItsOverride_NoLibraryRestriction()
    {
        AddLibrary("Movies", "/media/movies");
        var boxSet = Add(new BoxSet { ParentId = Add(new Folder { Path = "/media/movies" }).Id });

        AssertVisible(boxSet, User(), true);
    }

    [Fact]
    public void LegacyBoxSet_WithAPath_TakesTheStandardCheck()
    {
        AddLibrary("Movies", "/media/movies");
        var boxSet = Add(new BoxSet { Path = "/media/movies/saga", ParentId = Add(new Folder { Path = "/media/movies" }).Id });

        AssertVisible(boxSet, User(), false);
        AssertVisible(boxSet, User(all: true), true);
    }

    [Fact]
    public void SharedPlaylist_KeepsItsOverride_NoLibraryRestriction()
    {
        AddLibrary("Movies", "/media/movies");
        var owner = User();
        var playlist = Add(new Tesserafin.Controller.Playlists.Playlist { Path = "/config/data/playlists/list", OwnerUserId = owner.Id, ParentId = Add(new Folder { Path = "/media/movies" }).Id });

        AssertVisible(playlist, owner, true);
        AssertVisible(playlist, User(all: true), false);
    }

    [Fact]
    public void TwoUsers_SameItems_DifferentDecisions()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var vault = AddLibrary("Vault", "/media/vault", "/media/movies");
        var kids = AddLibrary("Kids", "/media/kids");
        var inBoth = MovieIn(movies);
        var vaultOnly = AddMovie(Add(new Folder { Path = "/media/vault" }), "/media/vault/b.mkv");
        var cartoon = MovieIn(kids);

        var admin = User(all: true);
        var child = User(blocked: [vault.Id]);
        var guest = User(enabled: [kids.Id]);

        var all = new[] { inBoth, vaultOnly, cartoon };
        Assert.Equal([true, true, true], all.Select(m => Decide(m, admin)));
        Assert.Equal([true, false, true], all.Select(m => Decide(m, child)));
        Assert.Equal([false, false, true], all.Select(m => Decide(m, guest)));
    }

    private static User User(bool all = false, Guid[]? enabled = null, Guid[]? blocked = null, string[]? blockedTags = null, string[]? allowedTags = null)
    {
        var user = new User("u", "auth", "reset") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.EnableAllFolders, all);
        user.SetPreference(PreferenceKind.EnabledFolders, enabled ?? []);
        user.SetPreference(PreferenceKind.BlockedMediaFolders, blocked ?? []);
        user.SetPreference(PreferenceKind.BlockedTags, blockedTags ?? []);
        user.SetPreference(PreferenceKind.AllowedTags, allowedTags ?? []);
        return user;
    }

    private bool Decide(BaseItem item, User user)
    {
        var access = _access.GetVisibleItemById<BaseItem>(item.Id, user) is not null;
        Assert.Equal(item.IsVisibleStandalone(user), access);
        return access;
    }

    private void AssertVisible(BaseItem item, User user, bool expected) => Assert.Equal(expected, Decide(item, user));

    private CollectionFolder AddLibrary(string name, params string[] locations)
    {
        var library = new CollectionFolder { Id = Guid.NewGuid(), Name = name, Path = "/config/root/" + name + "-" + Guid.NewGuid().ToString("N"), PhysicalLocationsList = locations };
        _libraries.Add(library);
        return Add(library);
    }

    private T Add<T>(T item)
        where T : BaseItem
    {
        if (item.Id.Equals(Guid.Empty))
        {
            item.Id = Guid.NewGuid();
        }

        _items[item.Id] = item;
        return item;
    }

    private Movie MovieIn(CollectionFolder library) =>
        AddMovie(Add(new Folder { Path = library.PhysicalLocations[0] }), library.PhysicalLocations[0] + "/a.mkv");

    private Movie AddMovie(Folder parent, string path, params string[] tags) => Add(new Movie { Path = path, ParentId = parent.Id, Tags = tags });
}
