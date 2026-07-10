using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Reefin.Controller.Configuration;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Entities.Movies;
using Reefin.Controller.IO;
using Reefin.Controller.Library;
using Reefin.Controller.LiveTv;
using Reefin.Controller.Persistence;
using Reefin.Controller.Providers;
using Reefin.Controller.Resolvers;
using Reefin.Controller.Sorting;
using Reefin.Data;
using Reefin.Database.Implementations.Entities;
using Reefin.Database.Implementations.Enums;
using Reefin.Model.Configuration;
using Reefin.Naming.Common;
using Xunit;

namespace Reefin.Server.Implementations.Tests.Library.LibraryManager;

/// <summary>
/// Characterization tests for <see cref="Reefin.Server.Core.Library.LibraryManager"/> item lookup
/// (<c>GetItemById</c> family) and its cache (<c>_cache</c>, a <c>FastConcurrentLru</c>). These tests
/// lock down the CURRENT behavior as groundwork for PR70 (which will fix the stale-cache bug in
/// <c>DeleteItemsUnsafeFast</c> documented below) - they are not a statement of the desired behavior.
/// </summary>
[Collection(LibraryManagerStaticStateFixture.Name)]
public class LibraryManagerItemLookupTests
{
    private readonly Reefin.Server.Core.Library.LibraryManager _libraryManager;
    private readonly Mock<IItemRepository> _itemRepositoryMock;
    private readonly Mock<IItemPersistenceService> _persistenceServiceMock;
    private readonly Mock<IServerConfigurationManager> _configurationManagerMock;
    private readonly Mock<IExternalDataManager> _externalDataManagerMock;

    public LibraryManagerItemLookupTests()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());

        _configurationManagerMock = fixture.Freeze<Mock<IServerConfigurationManager>>();
        _configurationManagerMock.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        _configurationManagerMock.Setup(c => c.ApplicationPaths.InternalMetadataPath).Returns("/data/metadata");
        _configurationManagerMock.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _itemRepositoryMock = fixture.Freeze<Mock<IItemRepository>>();
        _itemRepositoryMock.Setup(i => i.RetrieveItem(It.IsAny<Guid>())).Returns<BaseItem>(null);

        _persistenceServiceMock = fixture.Freeze<Mock<IItemPersistenceService>>();

        _externalDataManagerMock = fixture.Freeze<Mock<IExternalDataManager>>();
        fixture.Register(() => new Lazy<IExternalDataManager>(() => _externalDataManagerMock.Object));

        _libraryManager = fixture.Build<Reefin.Server.Core.Library.LibraryManager>().Do(s => s.AddParts(
                fixture.Create<IEnumerable<IResolverIgnoreRule>>(),
                fixture.Create<IEnumerable<IItemResolver>>(),
                fixture.Create<IEnumerable<IIntroProvider>>(),
                fixture.Create<IEnumerable<IBaseItemComparer>>(),
                fixture.Create<IEnumerable<ILibraryPostScanTask>>()))
            .Create();
    }

    private static void SetConfigurationManagerStatic(IServerConfigurationManager configurationManager)
    {
        BaseItem.ConfigurationManager = configurationManager;
    }

    private static void SetLibraryManagerStatic(ILibraryManager libraryManager)
    {
        BaseItem.LibraryManager = libraryManager;
    }

    // ---------------------------------------------------------------
    // 1. Empty guid
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemById_EmptyGuid_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _libraryManager.GetItemById(Guid.Empty));
    }

    // ---------------------------------------------------------------
    // 2-4. Cache hit / miss / cacheable types
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemById_ItemPreRegisteredInCache_DoesNotCallRepository()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Folder" };
        _libraryManager.RegisterItem(folder);

        var result = _libraryManager.GetItemById(folder.Id);

        Assert.Same(folder, result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void GetItemById_CacheMissCacheableItem_CallsRepositoryOnce()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Folder" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(folder.Id)).Returns(folder);

        var result = _libraryManager.GetItemById(folder.Id);

        Assert.Same(folder, result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(folder.Id), Times.Once);
    }

    public static TheoryData<BaseItem> CacheableItems()
    {
        return new TheoryData<BaseItem>
        {
            new Folder { Id = Guid.NewGuid(), Name = "Folder" },
            new Movie { Id = Guid.NewGuid(), Name = "Movie" },
            new LiveTvChannel { Id = Guid.NewGuid(), Name = "Channel" },
            new MusicArtist { Id = Guid.NewGuid(), Name = "Artist" },
        };
    }

    [Theory]
    [MemberData(nameof(CacheableItems))]
    public void GetItemById_CacheableTypeSecondCall_RepositoryCalledOnlyOnce(BaseItem item)
    {
        _itemRepositoryMock.Setup(r => r.RetrieveItem(item.Id)).Returns(item);

        var first = _libraryManager.GetItemById(item.Id);
        var second = _libraryManager.GetItemById(item.Id);

        Assert.Same(item, first);
        Assert.Same(item, second);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(item.Id), Times.Once);
    }

    // ---------------------------------------------------------------
    // 5. Non-cacheable types
    // ---------------------------------------------------------------

    public static TheoryData<BaseItem> NonCacheableItems()
    {
        return new TheoryData<BaseItem>
        {
            new Genre { Id = Guid.NewGuid(), Name = "Genre" }, // IItemByName, not MusicArtist
            new Person { Id = Guid.NewGuid(), Name = "Person" }, // IItemByName, not MusicArtist
            new Audio { Id = Guid.NewGuid(), Name = "Audio" }, // leaf, not Video/LiveTvChannel
            new Book { Id = Guid.NewGuid(), Name = "Book" }, // leaf, not Video/LiveTvChannel
        };
    }

    [Theory]
    [MemberData(nameof(NonCacheableItems))]
    public void GetItemById_NonCacheableType_RepositoryCalledOnEveryLookup(BaseItem item)
    {
        _itemRepositoryMock.Setup(r => r.RetrieveItem(item.Id)).Returns(item);

        _libraryManager.GetItemById(item.Id);
        _libraryManager.GetItemById(item.Id);

        _itemRepositoryMock.Verify(r => r.RetrieveItem(item.Id), Times.Exactly(2));
    }

    // ---------------------------------------------------------------
    // 6. Incompatible generic cast
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemByIdGeneric_IncompatibleType_ReturnsNull()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(movie.Id)).Returns(movie);

        var result = _libraryManager.GetItemById<MusicArtist>(movie.Id);

        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // 7. User-aware variant
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemByIdGeneric_UserNull_AlwaysReturnsItem()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie", Tags = new[] { "blocked" } };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(movie.Id)).Returns(movie);

        var result = _libraryManager.GetItemById<Movie>(movie.Id, (User)null!);

        Assert.Same(movie, result);
    }

    [Fact]
    public void GetItemByIdGeneric_ItemNotFound_ReturnsNull()
    {
        var id = Guid.NewGuid();
        _itemRepositoryMock.Setup(r => r.RetrieveItem(id)).Returns((BaseItem)null!);

        var user = new User("test-user", "provider", "provider");
        var result = _libraryManager.GetItemById<Movie>(id, user);

        Assert.Null(result);
    }

    [Fact]
    public void GetItemByIdGeneric_VisibleItemNoTagRestrictions_ReturnsItem()
    {
        // Audio (not Video) to avoid Video.SourceType touching the unset Video.RecordingsManager
        // static via IsActiveRecording(). No Path set -> IsVisibleStandaloneInternal short-circuits
        // to visible before touching collection folders / LibraryManager statics (topParent.Path is
        // empty -> return true).
        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track" };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(audio.Id)).Returns(audio);

        var user = new User("test-user", "provider", "provider");

        var result = _libraryManager.GetItemById<Audio>(audio.Id, user);

        Assert.Same(audio, result);
    }

    [Fact]
    public void GetItemByIdGeneric_InvisibleViaBlockedTag_ReturnsNull()
    {
        // Blocked-tag path calls BaseItem.GetInheritedTags(), which unconditionally consults
        // LibraryManager.GetCollectionFolders(this) - requires the static to be set.
        SetLibraryManagerStatic(Mock.Of<ILibraryManager>(m => m.GetCollectionFolders(It.IsAny<BaseItem>()) == new List<Folder>()));

        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Tags = new[] { "blocked" } };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(audio.Id)).Returns(audio);

        var user = new User("test-user", "provider", "provider");
        user.SetPreference(PreferenceKind.BlockedTags, new[] { "blocked" });

        var result = _libraryManager.GetItemById<Audio>(audio.Id, user);

        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // 8. UserRootFolder visibility exception
    // ---------------------------------------------------------------

    [Fact]
    public void GetItemByIdGeneric_UserRootFolderWithUser_AlwaysReturnsItem()
    {
        // ItemIsVisible short-circuits on "item is UserRootFolder" (OR), so IsVisibleStandalone is
        // never evaluated - the blocked tag below would normally hide the item but never gets checked.
        var rootFolder = new UserRootFolder { Id = Guid.NewGuid(), Name = "root", Tags = new[] { "blocked" } };
        _itemRepositoryMock.Setup(r => r.RetrieveItem(rootFolder.Id)).Returns(rootFolder);

        var user = new User("test-user", "provider", "provider");
        user.SetPreference(PreferenceKind.BlockedTags, new[] { "blocked" });

        var result = _libraryManager.GetItemById<UserRootFolder>(rootFolder.Id, user);

        Assert.Same(rootFolder, result);
    }

    // ---------------------------------------------------------------
    // 9. RegisterItem replaces the cached instance
    // ---------------------------------------------------------------

    [Fact]
    public void RegisterItem_NewInstanceSameId_ReplacesCachedInstance()
    {
        var id = Guid.NewGuid();
        var original = new Folder { Id = id, Name = "Original" };
        var replacement = new Folder { Id = id, Name = "Replacement" };

        _libraryManager.RegisterItem(original);
        Assert.Same(original, _libraryManager.GetItemById(id));

        _libraryManager.RegisterItem(replacement);

        var result = _libraryManager.GetItemById(id);
        Assert.Same(replacement, result);
        Assert.NotSame(original, result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void CreateItems_CacheableItem_RegistersInCache()
    {
        var artist = new MusicArtist { Id = Guid.NewGuid(), Name = "Artist" };

        _libraryManager.CreateItems(new List<BaseItem> { artist }, null, System.Threading.CancellationToken.None);

        var result = _libraryManager.GetItemById(artist.Id);

        Assert.Same(artist, result);
        _persistenceServiceMock.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    // ---------------------------------------------------------------
    // 10. DeleteItem invalidates the cache
    // ---------------------------------------------------------------

    [Fact]
    public void DeleteItem_CacheableItem_InvalidatesCache()
    {
        SetConfigurationManagerStatic(_configurationManagerMock.Object);

        // LiveTvChannel keeps DeleteItem's side paths inert: SourceType is LiveTV (not Channel, so
        // no ChannelManager static needed), IsFolder is false (no recursive children), and with
        // Path left null, IsFileProtocol/IsInternalItem are both false so no filesystem deletion is
        // attempted. This lets us exercise the cache-invalidation tail of DeleteItem without pulling
        // in the Video alternate-version machinery or real file I/O.
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "Channel" };
        _libraryManager.RegisterItem(channel);
        Assert.Same(channel, _libraryManager.GetItemById(channel.Id));

        _libraryManager.DeleteItem(channel, new DeleteOptions { DeleteFileLocation = false }, null!, false);

        _itemRepositoryMock.Setup(r => r.RetrieveItem(channel.Id)).Returns((BaseItem)null!);
        var result = _libraryManager.GetItemById(channel.Id);

        Assert.Null(result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(channel.Id), Times.Once);
        _persistenceServiceMock.Verify(p => p.DeleteItem(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(channel.Id))), Times.Once);
    }

    // ---------------------------------------------------------------
    // 11. DeleteItemsUnsafeFast does NOT invalidate the cache (known bug, PR70 will fix)
    // ---------------------------------------------------------------

    [Fact]
    public void DeleteItemsUnsafeFast_CacheableItem_DoesNotInvalidateCache()
    {
        SetConfigurationManagerStatic(_configurationManagerMock.Object);

        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "Channel" };
        _libraryManager.RegisterItem(channel);
        Assert.Same(channel, _libraryManager.GetItemById(channel.Id));

        _libraryManager.DeleteItemsUnsafeFast(new List<BaseItem> { channel }, deleteSourceFiles: false);

        // Comportement actuel (bug connu): PR70 ajoutera l'invalidation et inversera cette assertion.
        var result = _libraryManager.GetItemById(channel.Id);

        Assert.Same(channel, result);
        _itemRepositoryMock.Verify(r => r.RetrieveItem(It.IsAny<Guid>()), Times.Never);
        _persistenceServiceMock.Verify(p => p.DeleteItem(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(channel.Id))), Times.Once);
    }
}
