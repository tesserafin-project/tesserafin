using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Tesserafin.Controller.Channels;
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
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
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
    private readonly Tesserafin.Server.Core.Library.LibraryManager _manager;
    private Folder _root;
    private CollectionFolder? _sets;
    private Folder? _setsFolder;

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

        _root = new Folder { Id = Guid.NewGuid(), Children = _libraries };
        var rootProvider = Mock.Of<IUserRootFolderProvider>();
        Mock.Get(rootProvider).Setup(p => p.GetUserRootFolder()).Returns(() => _root);
        fixture.Register(() => rootProvider);

        var resolver = new Tesserafin.Server.Core.Library.CollectionFolderResolver(lookup, rootProvider);
        fixture.Register<ICollectionFolderResolver>(() => resolver);

        var manager = _manager = fixture.Build<Tesserafin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                Array.Empty<IResolverIgnoreRule>(),
                Array.Empty<IItemResolver>(),
                Array.Empty<IIntroProvider>(),
                fixture.Create<IEnumerable<IBaseItemComparer>>(),
                Array.Empty<ILibraryPostScanTask>()))
            .Create();

        _access = new Tesserafin.Server.Core.Library.ItemAccessService(lookup, new Tesserafin.Server.Core.Library.TagVisibilityService(lookup, resolver, rootProvider));

        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(LibraryOptions), It.IsAny<string>()))
            .Returns((Type _, string file) => _options.GetValueOrDefault(file)!);
        CollectionFolder.XmlSerializer = xml.Object;

        BaseItem.LibraryManager = manager;
        BaseItem.ChannelManager = Mock.Of<IChannelManager>();
        Mock.Get(BaseItem.ChannelManager).Setup(c => c.GetChannel(It.IsAny<string>())).Returns((string id) => (manager.GetItemById(id) as Channel)!);
        BaseItem.ItemRepository = repository.Object;
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
    public void BoxSet_WithoutLinkedChildren_NeedsOnlyItsHostingLibrary()
    {
        // No children, so no children's libraries to match: the hosting library alone decides (#320).
        var movies = AddLibrary("Movies", "/media/movies");
        var hostedInMovies = Add(new BoxSet { ParentId = Add(new Folder { Path = "/media/movies" }).Id });
        var empty = AddBoxSet();

        AssertVisible(hostedInMovies, User(), false);
        AssertVisible(hostedInMovies, User(enabled: [movies.Id]), true);
        AssertVisible(hostedInMovies, User(all: true), true);
        AssertVisible(empty, User(), false);
        AssertVisible(empty, User(enabled: [movies.Id]), false);
        AssertVisible(empty, User(enabled: [Sets().Id]), true);
        AssertVisible(empty, User(blocked: [Sets().Id]), false);
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

    [Fact]
    public void AccessPath_MovieWithParentsAndLibraryRestrictions_DecidesWithAStaticThatThrowsOnEveryCall()
    {
        var (saga, movies, vault, kids) = ProductionLikeGraph();
        var users = Users(movies, vault, kids);
        var expected = new[] { true, false, false, true, false, false, true, false, false, false, true };

        // Control: the static-backed path reads the throwing static.
        Assert.Equal(expected, users.Select(u => saga.IsVisibleStandalone(u)));
        ThrowingStatic();
        Assert.Throws<MockException>(() => saga.IsVisibleStandalone(users[0]));

        Assert.Equal(expected, users.Select(u => _access.GetVisibleItemById<BaseItem>(saga.Id, u) is not null));
    }

    [Fact]
    public void AccessPath_RatedMovie_TakesTheCountryCodeFromItsParentsAndLibrariesThroughTheService()
    {
        var (saga, movies, _, _) = ProductionLikeGraph();
        BaseItem.LocalizationManager = Mock.Of<ILocalizationManager>(l =>
            l.GetRatingScore("R", "FR") == new ParentalRatingScore(18, null)
            && l.GetRatingScore("R", "US") == new ParentalRatingScore(17, null));
        saga.OfficialRating = "R";
        var teen = User(enabled: [movies.Id]);
        teen.MaxParentalRatingScore = 17;

        Assert.True(saga.IsVisibleStandalone(teen));
        movies.PreferredMetadataCountryCode = "FR";
        Assert.False(saga.IsVisibleStandalone(teen));
        ThrowingStatic();

        Assert.Null(_access.GetVisibleItemById<BaseItem>(saga.Id, teen));
        movies.PreferredMetadataCountryCode = null;
        _items[saga.ParentId].PreferredMetadataCountryCode = "US";
        Assert.NotNull(_access.GetVisibleItemById<BaseItem>(saga.Id, teen));

        // Out of this slice: without a country code on the item, its parents or its libraries, the
        // rating falls back to the library options, still read through the static.
        _items[saga.ParentId].PreferredMetadataCountryCode = null;
        Assert.Throws<MockException>(() => _access.GetVisibleItemById<BaseItem>(saga.Id, teen));
    }

    [Fact]
    public void AccessPath_SameProcess_RightsAndTagChangesApplyWithoutRebuildingAnything()
    {
        var (saga, movies, vault, _) = ProductionLikeGraph();
        ThrowingStatic();
        var bea = User(enabled: [movies.Id]);
        var alex = User(all: true);

        Assert.Equal([true, true], Decisions(saga, bea, alex));

        bea.SetPreference(PreferenceKind.EnabledFolders, [Guid.NewGuid()]);
        Assert.Equal([false, true], Decisions(saga, bea, alex));

        bea.SetPreference(PreferenceKind.BlockedMediaFolders, [vault.Id]);
        Assert.Equal([true, true], Decisions(saga, bea, alex));

        alex.SetPreference(PreferenceKind.BlockedMediaFolders, [movies.Id, vault.Id]);
        Assert.Equal([true, false], Decisions(saga, bea, alex));

        alex.SetPreference(PreferenceKind.BlockedMediaFolders, []);
        bea.SetPreference(PreferenceKind.BlockedTags, ["vault only"]);
        movies.Tags = ["Vault Only"];
        Assert.Equal([false, true], Decisions(saga, bea, alex));

        movies.Tags = [];
        _items[saga.ParentId].Tags = ["vault only"];
        Assert.Equal([false, true], Decisions(saga, bea, alex));

        _items[saga.ParentId].Tags = [];
        Assert.Equal([true, true], Decisions(saga, bea, alex));
    }

    [Fact]
    public void BoxSet_LinkedChildren_AdmittedWhenOneOfTheirLibrariesIsTheUsers()
    {
        // The hosting library first, like any item's; then the user's visible libraries (enabled/blocked,
        // then options and tags), matched against its children's libraries.
        var movies = AddLibrary("Movies", "/media/movies");
        var vault = AddLibrary("Vault", "/media/vault", "/media/movies");
        var kids = AddLibrary("Kids", "/media/kids");
        var boxSet = AddBoxSet(MovieIn(movies));

        AssertVisible(boxSet, User(all: true), true);
        AssertVisible(boxSet, User(enabled: [vault.Id, Sets().Id]), true);
        AssertVisible(boxSet, User(enabled: [kids.Id, Sets().Id]), false);
        AssertVisible(boxSet, User(blocked: [movies.Id]), true);
        AssertVisible(boxSet, User(blocked: [movies.Id, vault.Id]), false);
        AssertVisible(boxSet, User(blocked: [kids.Id], enabled: [kids.Id]), true);
        AssertVisible(boxSet, User(), false);

        // The hosting library is required, and grants nothing on the children by itself (#320).
        AssertVisible(boxSet, User(enabled: [vault.Id]), false);
        AssertVisible(boxSet, User(enabled: [movies.Id, vault.Id]), false);
        AssertVisible(boxSet, User(enabled: [Sets().Id]), false);
        AssertVisible(boxSet, User(blocked: [Sets().Id]), false);
        AssertVisible(boxSet, User(all: true, blocked: [Sets().Id]), false);
        AssertVisible(boxSet, User(blocked: [Sets().Id], enabled: [movies.Id, Sets().Id]), false);
    }

    [Fact]
    public void BoxSet_DisabledOrTaggedLibrary_IsNotTheUsers()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var boxSet = AddBoxSet(MovieIn(movies));

        var disabled = AddLibrary("Disabled", "/media/disabled");
        _options[System.IO.Path.Combine(disabled.Path, "options.xml")] = new LibraryOptions { Enabled = false };

        movies.Tags = ["vault only"];
        AssertVisible(boxSet, User(all: true, blockedTags: ["vault only"]), false);
        movies.Tags = [];
        Sets().Tags = ["vault only"];
        AssertVisible(boxSet, User(all: true, blockedTags: ["vault only"]), false);
        AssertVisible(boxSet, User(all: true), true);
        Sets().Tags = [];
        movies.Tags = ["vault only"];
        AssertVisible(boxSet, User(all: true, allowedTags: ["other"]), false);
        AssertVisible(AddBoxSet(MovieIn(disabled)), User(all: true), false);

        // The libraries are checked with the allowed tags too: a box set carrying the user's allowed tag
        // still needs a library that carries one. (Under the aggregate root, as in production, a library
        // skips the allowed tags.)
        movies.Tags = [];
        boxSet.Tags = ["family"];
        AssertVisible(boxSet, User(all: true, allowedTags: ["family"]), false);
        movies.Tags = ["family"];
        AssertVisible(boxSet, User(all: true, allowedTags: ["family"]), true);
    }

    [Fact]
    public void BoxSet_ChildrenOutsideEveryLibrary_AreNotFolderRestricted()
    {
        AddLibrary("Movies", "/media/movies");
        var boxSet = AddBoxSet(AddMovie(Add(new Folder { Path = "/media/other" }), "/media/other/a.mkv"));

        AssertVisible(boxSet, User(enabled: [Sets().Id]), true);
        AssertVisible(boxSet, User(), false);
    }

    [Fact]
    public void BoxSet_LinkedChildNotFound_ContributesNoLibrary_TheHostingLibraryStillApplies()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var boxSet = AddBoxSet();
        boxSet.LinkedChildren = [new LinkedChild { ItemId = Guid.NewGuid() }];

        // The search by path logs the miss.
        BaseItem.Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<BaseItem>.Instance;

        AssertVisible(boxSet, User(enabled: [Sets().Id]), true);
        AssertVisible(boxSet, User(enabled: [movies.Id]), false);
        AssertVisible(boxSet, User(all: true, blocked: [Sets().Id]), false);
    }

    [Fact]
    public void BoxSet_StoredLibraryFolderIds_ReplaceItsChildrensLibraries()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var kids = AddLibrary("Kids", "/media/kids");
        var boxSet = AddBoxSet(MovieIn(movies));

        boxSet.LibraryFolderIds = [kids.Id];
        AssertVisible(boxSet, User(enabled: [kids.Id, Sets().Id]), true);
        AssertVisible(boxSet, User(enabled: [movies.Id, Sets().Id]), false);

        // The stored ids are the children's libraries, never the hosting one.
        boxSet.LibraryFolderIds = [Sets().Id];
        AssertVisible(boxSet, User(enabled: [Sets().Id]), true);
        AssertVisible(boxSet, User(enabled: [movies.Id]), false);

        boxSet.LibraryFolderIds = [];
        AssertVisible(boxSet, User(enabled: [Sets().Id]), true);
        AssertVisible(boxSet, User(), false);
    }

    [Fact]
    public void BoxSet_NestedBoxSets_ContributeTheirChildrensLibraries_CycleIncluded()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        var kids = AddLibrary("Kids", "/media/kids");
        var shows = AddLibrary("Shows", "/media/shows");
        var inner = AddBoxSet(MovieIn(kids));
        var outer = AddBoxSet(MovieIn(movies), inner);
        inner.LinkedChildren = [.. inner.LinkedChildren, LinkedChild.Create(outer)];

        AssertVisible(outer, User(enabled: [kids.Id, Sets().Id]), true);
        AssertVisible(inner, User(enabled: [movies.Id, Sets().Id]), true);
        AssertVisible(outer, User(enabled: [shows.Id, Sets().Id]), false);
        AssertVisible(outer, User(enabled: [kids.Id]), false);
    }

    [Fact]
    public void BoxSet_WithAMaximumRating_HiddenOnlyWhenEveryChildIsAboveIt()
    {
        var movies = AddLibrary("Movies", "/media/movies");
        movies.PreferredMetadataCountryCode = "US";
        RatingScores();
        var adult = MovieIn(movies);
        adult.OfficialRating = "R";
        var family = MovieIn(movies);
        family.OfficialRating = "PG";
        var teen = User(all: true);
        teen.MaxParentalRatingScore = 12;

        AssertVisible(AddBoxSet(adult), teen, false);
        AssertVisible(AddBoxSet(adult, family), teen, true);
        AssertVisible(AddBoxSet(adult), User(all: true), true);
    }

    [Fact]
    public void AccessPath_BoxSet_DecidesWithAStaticThatThrowsOnEveryCall()
    {
        var (saga, movies, vault, kids) = ProductionLikeGraph();

        // A country code on each library keeps the rating off its library options fallback (static, out of slice).
        movies.PreferredMetadataCountryCode = "US";
        kids.PreferredMetadataCountryCode = "US";
        RatingScores();
        var family = MovieIn(kids);
        family.OfficialRating = "PG";
        saga.OfficialRating = "R";
        var rated = AddBoxSet(saga, family);
        var nested = AddBoxSet(AddBoxSet(family));
        var users = Users(movies, vault, kids);
        foreach (var user in users)
        {
            user.MaxParentalRatingScore = 12;
        }

        // The rated children make the parental step read them, the nested box set the flattening. Saga
        // belongs to Movies only (its parent chain), so Vault alone does not admit the box set; and no
        // enabled-libraries list here names the hosting library, so none admits a box set (#320).
        var expectedRated = new[] { true, false, false, true, true, true, true, false, true, false, false };
        var expectedNested = new[] { true, false, false, true, true, true, false, false, true, false, false };
        Assert.Equal(expectedRated, users.Select(u => rated.IsVisibleStandalone(u)));
        Assert.Equal(expectedNested, users.Select(u => nested.IsVisibleStandalone(u)));
        family.OfficialRating = "R";
        Assert.All(users, u => Assert.False(rated.IsVisibleStandalone(u)));
        family.OfficialRating = "PG";
        ThrowingStatic();
        Assert.Throws<MockException>(() => rated.IsVisibleStandalone(users[0]));

        Assert.Equal(expectedRated, Decisions(rated, users));
        Assert.Equal(expectedNested, Decisions(nested, users));
        family.OfficialRating = "R";
        Assert.All(Decisions(rated, users), Assert.False);

        // Out of this slice: a linked child the lookup does not find is searched by path, through the static.
        rated.LinkedChildren = [.. rated.LinkedChildren, new LinkedChild { ItemId = Guid.NewGuid() }];
        Assert.Throws<MockException>(() => _access.GetVisibleItemById<BaseItem>(rated.Id, users[0]));
    }

    [Fact]
    public void AccessPath_BoxSet_HostingLibraryRightsApplyWithoutRebuildingAnything()
    {
        var (saga, movies, _, kids) = ProductionLikeGraph();
        var boxSet = AddBoxSet(saga);
        ThrowingStatic();
        var bea = User(enabled: [movies.Id]);
        var alex = User(all: true);

        Assert.Equal([false, true], Decisions(boxSet, bea, alex));

        bea.SetPreference(PreferenceKind.EnabledFolders, [movies.Id, Sets().Id]);
        Assert.Equal([true, true], Decisions(boxSet, bea, alex));

        bea.SetPreference(PreferenceKind.EnabledFolders, [kids.Id, Sets().Id]);
        Assert.Equal([false, true], Decisions(boxSet, bea, alex));

        bea.SetPermission(PermissionKind.EnableAllFolders, true);
        alex.SetPreference(PreferenceKind.BlockedMediaFolders, [Sets().Id]);
        Assert.Equal([true, false], Decisions(boxSet, bea, alex));

        alex.SetPreference(PreferenceKind.BlockedMediaFolders, []);
        Assert.Equal([true, true], Decisions(boxSet, bea, alex));
    }

    [Fact]
    public void ChannelContent_EnabledAndBlockedChannels_DecideTheItemAndTheChannel()
    {
        var (channel, item) = ChannelWithItem();
        var other = Guid.NewGuid();

        // A blocked-channels list, when the user has one, replaces both the enable-all permission and the enabled list.
        var users = new[]
        {
            ChannelUser(all: true),
            ChannelUser(enabled: [channel.Id]),
            ChannelUser(enabled: [other]),
            ChannelUser(),
            ChannelUser(all: true, blocked: [channel.Id]),
            ChannelUser(blocked: [other]),
            ChannelUser(enabled: [channel.Id], blocked: [channel.Id]),
        };
        var expected = new[] { true, true, false, false, false, true, false };

        Assert.Equal(expected, users.Select(u => Decide(item, u)));
        Assert.Equal(expected, users.Select(u => Decide(channel, u)));
    }

    [Fact]
    public void ChannelContent_ItemTags_ChannelTags_AndAllowedTagsOnBoth()
    {
        var (channel, item) = ChannelWithItem();

        AssertVisible(item, ChannelUser(all: true, blockedTags: ["late"]), true);
        item.Tags = ["late"];
        AssertVisible(item, ChannelUser(all: true, blockedTags: ["late"]), false);
        item.Tags = [];
        channel.Tags = ["late"];
        AssertVisible(item, ChannelUser(all: true, blockedTags: ["late"]), false);

        // The channel is checked with allowed tags applied, so an allowed tag on the item alone is not enough.
        channel.Tags = [];
        item.Tags = ["kids"];
        AssertVisible(item, ChannelUser(all: true, allowedTags: ["kids"]), false);
        channel.Tags = ["kids"];
        AssertVisible(item, ChannelUser(all: true, allowedTags: ["kids"]), true);
    }

    [Fact]
    public void ChannelContent_ARatedChannel_HidesItsContentAboveTheUsersMaximum()
    {
        var (channel, item) = ChannelWithItem();
        RatingScores();
        channel.PreferredMetadataCountryCode = "US";
        item.PreferredMetadataCountryCode = "US";
        channel.OfficialRating = "R";
        var teen = ChannelUser(all: true);
        teen.MaxParentalRatingScore = 12;

        AssertVisible(item, teen, false);
        channel.OfficialRating = "PG";
        AssertVisible(item, teen, true);
        item.OfficialRating = "R";
        AssertVisible(item, teen, false);
    }

    [Fact]
    public void ChannelContent_ChannelNotFound_Throws_OnBothPaths()
    {
        // Pre-existing: a channel id that resolves to nothing is dereferenced, not denied.
        var item = Add(new Movie { Path = "http://channel.invalid/a.mp4", ChannelId = Guid.NewGuid() });
        var user = ChannelUser(all: true);

        Assert.Throws<NullReferenceException>(() => item.IsVisibleStandalone(user));
        Assert.Throws<NullReferenceException>(() => _access.GetVisibleItemById<BaseItem>(item.Id, user));
    }

    [Fact]
    public void AccessPath_ChannelContent_DecidesWithStaticsThatThrowOnEveryCall()
    {
        var (channel, item) = ChannelWithItem();
        channel.Tags = ["kids"];
        var users = new[]
        {
            ChannelUser(all: true),
            ChannelUser(enabled: [channel.Id]),
            ChannelUser(),
            ChannelUser(all: true, blocked: [channel.Id]),
            ChannelUser(all: true, blockedTags: ["kids"]),
            ChannelUser(all: true, allowedTags: ["kids"]),
        };
        // The item inherits the channel's tags (its parent), so the allowed tag on the channel admits it.
        var expected = new[] { true, true, false, false, false, true };
        Assert.Equal(expected, users.Select(u => item.IsVisibleStandalone(u)));

        // Both statics of the channel branch throw on any call: the channel lookup and the library manager.
        ThrowingStatic();
        BaseItem.ChannelManager = new Mock<IChannelManager>(MockBehavior.Strict).Object;
        Assert.Throws<MockException>(() => item.IsVisibleStandalone(users[0]));

        Assert.Equal(expected, Decisions(item, users));
        Assert.Equal(expected, Decisions(channel, users));

        // Same process, nothing rebuilt: rights and tags apply on the next decision.
        users[2].SetPreference(PreferenceKind.EnabledChannels, [channel.Id]);
        item.Tags = ["kids"];
        Assert.Equal([true, true, true, false, false, true], Decisions(item, users));
    }

    private static void RatingScores() => BaseItem.LocalizationManager = Mock.Of<ILocalizationManager>(l =>
        l.GetRatingScore("R", "US") == new ParentalRatingScore(17, null)
        && l.GetRatingScore("PG", "US") == new ParentalRatingScore(10, null));

    /// <summary>
    /// The library that hosts the box sets, the production shape: a box-set library over the
    /// collections directory, whose physical folder is each box set's parent. Not named "Collections",
    /// so nothing may depend on the name.
    /// </summary>
    private CollectionFolder Sets() => _sets ??= AddLibrary("Sets", "/config/data/collections");

    private BoxSet AddBoxSet(params BaseItem[] linked) => Add(new BoxSet
    {
        Path = "/config/data/collections/" + Guid.NewGuid().ToString("N") + " [boxset]",
        ParentId = (_setsFolder ??= Add(new Folder { Path = Sets().PhysicalLocations[0] })).Id,
        LinkedChildren = linked.Select(LinkedChild.Create).ToArray(),
    });

    private bool[] Decisions(BaseItem item, params User[] users) =>
        users.Select(u => _access.GetVisibleItemById<BaseItem>(item.Id, u) is not null).ToArray();

    /// <summary>
    /// The production shape: an aggregate root holding the libraries, a <see cref="UserRootFolder"/>
    /// whose children are those libraries, and a movie in a tagged folder of the Movies library.
    /// </summary>
    private (Movie Saga, CollectionFolder Movies, CollectionFolder Vault, CollectionFolder Kids) ProductionLikeGraph()
    {
        var aggregate = Add(new AggregateFolder { Path = "/config/root" });
        var movies = AddLibrary("Movies", "/media/movies");
        var vault = AddLibrary("Vault", "/media/vault", "/media/movies");
        var kids = AddLibrary("Kids", "/media/kids");
        Sets();
        foreach (var library in _libraries)
        {
            library.ParentId = aggregate.Id;
        }

        // The user's root lists the aggregate root's virtual children too (a box set never belongs to one).
        aggregate.AddVirtualChild(Add(new Tesserafin.Server.Core.Playlists.PlaylistsFolder { Path = "/config/data/playlists", ParentId = aggregate.Id }));
        typeof(Tesserafin.Server.Core.Library.LibraryManager).GetField("_rootFolder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(_manager, aggregate);

        var userRoot = Add(new UserRootFolder { Path = "/config/data/root/default" });
        Mock.Get(BaseItem.ItemRepository).Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(userRoot.Id)))).Returns(_libraries);
        _root = userRoot;
        Assert.Equal(_libraries, userRoot.Children);

        var middle = Add(new Folder { Path = "/media/movies/saga", ParentId = movies.Id, Tags = ["saga"] });
        return (AddMovie(middle, "/media/movies/saga/a.mkv"), movies, vault, kids);
    }

    private static User[] Users(CollectionFolder movies, CollectionFolder vault, CollectionFolder kids) =>
    [
        User(all: true),
        User(enabled: [vault.Id]),
        User(enabled: [kids.Id]),
        User(blocked: [vault.Id]),
        User(blocked: [movies.Id, vault.Id]),
        User(all: true, blocked: [movies.Id, vault.Id], enabled: [movies.Id]),
        User(blocked: [kids.Id], enabled: [kids.Id]),
        User(),
        User(all: true, blockedTags: ["saga"]),
        User(all: true, allowedTags: ["other"], blockedTags: ["x"]),
        User(enabled: [movies.Id]),
    ];

    private static void ThrowingStatic() => BaseItem.LibraryManager = new Mock<ILibraryManager>(MockBehavior.Strict).Object;

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

    /// <summary>
    /// The production shape of channel content: a channel under no parent whose channel id is its own
    /// id, under its internal metadata path, and an item of that channel parented to it, with a remote path.
    /// </summary>
    private (Channel Channel, Movie Item) ChannelWithItem()
    {
        var id = Guid.NewGuid();
        var channel = Add(new Channel { Id = id, ChannelId = id, Path = Channel.GetInternalMetadataPath("/config/metadata", id) });
        return (channel, Add(new Movie { Path = "http://channel.invalid/a.mp4", ChannelId = id, ParentId = id }));
    }

    private static User ChannelUser(bool all = false, Guid[]? enabled = null, Guid[]? blocked = null, string[]? blockedTags = null, string[]? allowedTags = null)
    {
        var user = User(all: true, blockedTags: blockedTags, allowedTags: allowedTags);
        user.SetPermission(PermissionKind.EnableAllChannels, all);
        user.SetPreference(PreferenceKind.EnabledChannels, enabled ?? []);
        user.SetPreference(PreferenceKind.BlockedChannels, blocked ?? []);
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
