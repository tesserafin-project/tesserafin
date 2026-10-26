using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Entities;
using Tesserafin.Database.Implementations.Enums;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Server.Implementations.Item;
using Tesserafin.Server.Implementations.Tests.Library.LibraryManager;
using Xunit;
using DbLinkedChildType = Tesserafin.Database.Implementations.Entities.LinkedChildType;

namespace Tesserafin.Server.Implementations.Tests.Item;

[Collection(LibraryManagerStaticStateFixture.Name)]
public sealed class ItemPersistenceConservationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TesserafinDbContext> _options;
    private readonly ILibraryManager _previousLibraryManager;
    private readonly IRecordingsManager _previousRecordingsManager;
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly ItemPersistenceService _service;
    private readonly Guid _userId;
    private readonly Guid _otherUserId;
    private readonly Guid _packId;

    public ItemPersistenceConservationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite(_connection, options => options.MigrationsAssembly(typeof(SqliteDatabaseProvider).Assembly))
            .Options;
        using var context = CreateContext();
        context.Database.Migrate();
        var user = new User("viewer", "auth", "reset");
        var otherUser = new User("other-viewer", "auth", "reset");
        _userId = user.Id;
        _otherUserId = otherUser.Id;
        _packId = Guid.NewGuid();
        context.Users.AddRange(user, otherUser);
        context.ContentPacks.Add(new ContentPack
        {
            Id = _packId,
            Name = "Kept pack",
            NormalizedName = ContentPack.Normalize("Kept pack"),
            DateCreated = DateTime.UtcNow
        });
        context.SaveChanges();

        _previousLibraryManager = BaseItem.LibraryManager;
        _previousRecordingsManager = Video.RecordingsManager;
        _libraryManager.Setup(value => value.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => _items.GetValueOrDefault(id)!);
        _libraryManager.Setup(value => value.GetCollectionFolders(It.IsAny<BaseItem>()))
            .Returns(() => []);
        BaseItem.LibraryManager = _libraryManager.Object;
        Video.RecordingsManager = new Mock<IRecordingsManager>().Object;

        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(value => value.CreateDbContext()).Returns(CreateContext);
        var appHost = new Mock<IServerApplicationHost>();
        appHost.Setup(value => value.ReverseVirtualPath(It.IsAny<string>())).Returns((string path) => path);
        _service = new ItemPersistenceService(factory.Object, appHost.Object, NullLogger<ItemPersistenceService>.Instance);
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = _previousLibraryManager;
        Video.RecordingsManager = _previousRecordingsManager;
        _connection.Dispose();
    }

    [Fact]
    public void DeleteItem_MissingAncestorProjection_ConservesAllCascadedHistories()
    {
        var root = Item("root", isFolder: true);
        var child = Item("child", parentId: root.Id);
        // Cascade traversal must not stop at a row whose IsFolder flag is false.
        var grandchild = Item("grandchild", parentId: child.Id);
        var linked = Item("linked-only");
        Seed(root, child, grandchild, linked);
        Link(root.Id, linked.Id, DbLinkedChildType.Manual);

        _service.DeleteItem(root.Id);

        AssertForeignKeys();
        AssertDeletedAndDetached(root, child, grandchild);
        AssertKept(linked);
    }

    [Fact]
    public void DeleteItem_StaleAncestorProjection_DoesNotDeleteAnUnownedLinkedItem()
    {
        var root = Item("root", isFolder: true);
        var otherParent = Item("other-parent", isFolder: true);
        var unrelated = Item("unrelated", parentId: otherParent.Id);
        Seed(root, otherParent, unrelated);
        Link(root.Id, unrelated.Id, DbLinkedChildType.Manual);
        using (var context = CreateContext())
        {
            context.AncestorIds.Add(Ancestor(unrelated.Id, root.Id));
            context.SaveChanges();
        }

        _service.DeleteItem(root.Id);

        AssertDeletedAndDetached(root);
        AssertKept(otherParent, unrelated);
        using var verification = CreateContext();
        Assert.Empty(verification.AncestorIds.Where(row => row.ParentItemId.Equals(root.Id)));
        AssertForeignKeys();
    }

    [Fact]
    public void DeleteItem_NestedOwnedItemsAndTheirChildren_ArePreparedBeforeDeletion()
    {
        var root = Item("root", isFolder: true);
        var extra = Item("extra", ownerId: root.Id);
        var nestedExtra = Item("nested-extra", ownerId: extra.Id);
        var childOfExtra = Item("extra-child", parentId: nestedExtra.Id);
        var extraOfChild = Item("child-extra", ownerId: childOfExtra.Id);
        var kept = Item("kept");
        Seed(root, extra, nestedExtra, childOfExtra, extraOfChild, kept);

        _service.DeleteItem(root.Id);

        AssertDeletedAndDetached(root, extra, nestedExtra, childOfExtra, extraOfChild);
        AssertKept(kept);
        AssertForeignKeys();
    }

    [Fact]
    public void DeleteItem_PlaceholderInThePersistedOwnershipScope_RejectsWithoutChangingData()
    {
        var root = Item("root", isFolder: true);
        Seed(root);
        using (var context = CreateContext())
        {
            context.BaseItems.Single(row => row.Id.Equals(BaseItemRepository.PlaceholderId)).ParentId = root.Id;
            context.SaveChanges();
        }

        var before = Snapshot();
        var error = Assert.Throws<InvalidOperationException>(() => _service.DeleteItem(root.Id));
        Assert.Contains("placeholder", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
        AssertForeignKeys();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteItem_BatchOrSuccessiveDeletes_ResolveSharedKeysAndKeepUnrelatedHistory(bool batch)
    {
        var first = Item("first");
        var second = Item("second");
        var kept = Item("kept");
        Seed(first, second, kept);
        using (var context = CreateContext())
        {
            foreach (var userId in new[] { _userId, _otherUserId })
            {
                foreach (var (id, day) in new[] { (BaseItemRepository.PlaceholderId, 1), (first.Id, 2), (second.Id, 3) })
                {
                    context.UserData.Add(new UserData
                    {
                        ItemId = id,
                        Item = null,
                        UserId = userId,
                        User = null,
                        CustomDataKey = "shared-content-key",
                        LastPlayedDate = new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc),
                        PlayCount = day,
                        PlaybackPositionTicks = userId.Equals(_userId) ? 100 : 200
                    });
                }
            }

            context.SaveChanges();
        }

        if (batch)
        {
            _service.DeleteItem([first.Id, second.Id]);
        }
        else
        {
            _service.DeleteItem(first.Id);
            _service.DeleteItem(second.Id);
        }

        AssertDeletedAndDetached(first, second);
        AssertKept(kept);
        using var verification = CreateContext();
        var sharedRows = verification.UserData.Where(row => row.CustomDataKey == "shared-content-key").ToArray();
        Assert.Equal(2, sharedRows.Length);
        Assert.All(sharedRows, row =>
        {
            Assert.Equal(BaseItemRepository.PlaceholderId, row.ItemId);
            Assert.Equal(3, row.PlayCount);
            Assert.Equal(row.UserId.Equals(_userId) ? 100 : 200, row.PlaybackPositionTicks);
        });
        AssertForeignKeys();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveItems_NewParentAndChildInOneBatch_PersistsOnlyValidAncestors(bool childFirst)
    {
        var parent = new Folder { Id = Guid.NewGuid(), Name = "new-parent", SortName = "new-parent" };
        var child = new Movie { Id = Guid.NewGuid(), Name = "new-child", SortName = "new-child", ParentId = parent.Id };
        var absentLibrary = new Folder { Id = Guid.NewGuid(), Name = "not-persisted" };
        _items[parent.Id] = parent;
        _items[child.Id] = child;
        _libraryManager.Setup(value => value.GetCollectionFolders(child)).Returns([absentLibrary]);

        _service.SaveItems(childFirst ? [child, parent] : [parent, child], CancellationToken.None);

        using var context = CreateContext();
        var storedChild = context.BaseItems.Single(row => row.Id.Equals(child.Id));
        Assert.Equal(parent.Id, storedChild.ParentId);
        var ancestor = Assert.Single(context.AncestorIds.Where(row => row.ItemId.Equals(child.Id)));
        Assert.Equal(parent.Id, ancestor.ParentItemId);
        AssertForeignKeys();
    }

    [Fact]
    public void SaveItems_RemovedLocalVersion_ConservesItsHistoryAndDescendants()
    {
        var video = new Movie { Id = Guid.NewGuid(), Name = "updated-video", SortName = "updated-video" };
        var primary = Item("primary", id: video.Id);
        var local = Item("local-version", ownerId: video.Id);
        var localChild = Item("local-child", parentId: local.Id);
        var linked = Item("linked-version");
        Seed(primary, local, localChild, linked);
        Link(video.Id, local.Id, DbLinkedChildType.LocalAlternateVersion);
        Link(video.Id, linked.Id, DbLinkedChildType.LinkedAlternateVersion);

        _service.SaveItems([video], CancellationToken.None);

        AssertForeignKeys();
        AssertDeletedAndDetached(local, localChild);
        AssertKept(primary, linked);
        using var context = CreateContext();
        Assert.Equal("updated-video", context.BaseItems.Single(row => row.Id.Equals(video.Id)).Name);
        Assert.Empty(context.LinkedChildren.Where(row => row.ParentId.Equals(video.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deletion_CleansOnlyValuesMadeUnreferencedByThisOperation(bool saveLocalVersion)
    {
        var primary = Item("primary");
        var owned = Item("owned", ownerId: primary.Id);
        var kept = Item("kept");
        Seed(primary, owned, kept);
        Link(primary.Id, owned.Id, DbLinkedChildType.LocalAlternateVersion);
        var exclusive = Value("exclusive");
        var shared = Value("shared");
        var unrelated = Value("unrelated-unmapped");
        using (var context = CreateContext())
        {
            context.ItemValues.AddRange(exclusive, shared, unrelated);
            context.ItemValuesMap.AddRange(
                ValueMap(owned.Id, exclusive.ItemValueId),
                ValueMap(owned.Id, shared.ItemValueId),
                ValueMap(kept.Id, shared.ItemValueId));
            context.SaveChanges();
        }

        if (saveLocalVersion)
        {
            _service.SaveItems([new Movie { Id = primary.Id, Name = "primary", SortName = "primary" }], CancellationToken.None);
        }
        else
        {
            _service.DeleteItem(owned.Id);
        }

        using var verification = CreateContext();
        Assert.Single(verification.ItemValues.Where(row => row.ItemValueId.Equals(unrelated.ItemValueId)));
        Assert.Empty(verification.ItemValues.Where(row => row.ItemValueId.Equals(exclusive.ItemValueId)));
        Assert.Single(verification.ItemValues.Where(row => row.ItemValueId.Equals(shared.ItemValueId)));
        var remainingMap = Assert.Single(verification.ItemValuesMap);
        Assert.Equal(kept.Id, remainingMap.ItemId);
        Assert.Equal(shared.ItemValueId, remainingMap.ItemValueId);
        AssertDeletedAndDetached(owned);
        AssertKept(primary, kept);
        AssertForeignKeys();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureAfterDetachmentAndMembershipDeletion_RestoresTheWholeDatabase(bool saveLocalVersion)
    {
        var primary = Item("primary");
        var owned = Item("owned", ownerId: primary.Id);
        var kept = Item("kept");
        Seed(primary, owned, kept);
        Link(primary.Id, owned.Id, DbLinkedChildType.LocalAlternateVersion);
        using (var context = CreateContext())
        {
            // The message proves the failure is reached AFTER effective UserData and membership
            // mutations, rather than merely throwing before the first database change.
            context.Database.ExecuteSqlRaw("""
                CREATE TRIGGER fail_after_preparation BEFORE DELETE ON BaseItems
                WHEN OLD.Name = 'owned'
                BEGIN
                  SELECT CASE WHEN
                    EXISTS (SELECT 1 FROM UserData
                      WHERE ItemId = '00000000-0000-0000-0000-000000000001'
                        AND CustomDataKey = OLD.Id)
                    AND NOT EXISTS (SELECT 1 FROM ContentPackMemberships WHERE ItemId = OLD.Id)
                  THEN RAISE(ABORT, 'after-detach-and-membership-delete')
                  ELSE RAISE(ABORT, 'before-preparation') END;
                END;
                """);
        }

        var before = Snapshot();
        var error = Assert.ThrowsAny<Exception>(() =>
        {
            if (saveLocalVersion)
            {
                _service.SaveItems([new Movie { Id = primary.Id, Name = "must-roll-back", SortName = "must-roll-back" }], CancellationToken.None);
            }
            else
            {
                _service.DeleteItem(primary.Id);
            }
        });

        Assert.Contains("after-detach-and-membership-delete", error.GetBaseException().Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
        AssertForeignKeys();
    }

    private void Seed(params BaseItemEntity[] items)
    {
        using var context = CreateContext();
        context.BaseItems.AddRange(items);
        foreach (var item in items)
        {
            foreach (var userId in new[] { _userId, _otherUserId })
            {
                context.UserData.Add(new UserData
                {
                    ItemId = item.Id,
                    Item = null,
                    UserId = userId,
                    User = null,
                    CustomDataKey = Key(item.Id),
                    PlaybackPositionTicks = 123456,
                    PlayCount = userId.Equals(_userId) ? 3 : 7,
                    IsFavorite = true,
                    Rating = 7.5,
                    Likes = true,
                    LastPlayedDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                    AudioStreamIndex = 2,
                    SubtitleStreamIndex = 4
                });
            }

            context.ContentPackMemberships.Add(new ContentPackMembership { PackId = _packId, ItemId = item.Id });
            context.MediaSegments.Add(new MediaSegment { Id = Guid.NewGuid(), ItemId = item.Id, SegmentProviderId = "test" });
        }

        context.SaveChanges();
    }

    private void AssertDeletedAndDetached(params BaseItemEntity[] items)
    {
        using var context = CreateContext();
        foreach (var item in items)
        {
            Assert.DoesNotContain(context.BaseItems, row => row.Id.Equals(item.Id));
            foreach (var userId in new[] { _userId, _otherUserId })
            {
                var data = context.UserData.Single(row => row.UserId.Equals(userId) && row.CustomDataKey == Key(item.Id));
                Assert.Equal(BaseItemRepository.PlaceholderId, data.ItemId);
                Assert.NotNull(data.RetentionDate);
                Assert.Equal(123456, data.PlaybackPositionTicks);
                Assert.Equal(userId.Equals(_userId) ? 3 : 7, data.PlayCount);
                Assert.True(data.IsFavorite);
                Assert.Equal(7.5, data.Rating);
                Assert.True(data.Likes);
                Assert.False(data.Played);
                Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), data.LastPlayedDate);
                Assert.Equal(2, data.AudioStreamIndex);
                Assert.Equal(4, data.SubtitleStreamIndex);
            }

            Assert.Empty(context.ContentPackMemberships.Where(row => row.ItemId.Equals(item.Id)));
            Assert.Empty(context.MediaSegments.Where(row => row.ItemId.Equals(item.Id)));
        }

        Assert.Single(context.ContentPacks);
    }

    private void AssertKept(params BaseItemEntity[] items)
    {
        using var context = CreateContext();
        foreach (var item in items)
        {
            Assert.Single(context.BaseItems.Where(row => row.Id.Equals(item.Id)));
            Assert.Single(context.ContentPackMemberships.Where(row => row.ItemId.Equals(item.Id)));
            Assert.Single(context.MediaSegments.Where(row => row.ItemId.Equals(item.Id)));
            var histories = context.UserData.Where(row => row.ItemId.Equals(item.Id)).ToArray();
            Assert.Equal(2, histories.Length);
            Assert.All(histories, row => Assert.Null(row.RetentionDate));
            foreach (var userId in new[] { _userId, _otherUserId })
            {
                var data = Assert.Single(histories, row => row.UserId.Equals(userId));
                Assert.Equal(Key(item.Id), data.CustomDataKey);
                Assert.Equal(userId.Equals(_userId) ? 3 : 7, data.PlayCount);
                Assert.Equal(123456, data.PlaybackPositionTicks);
                Assert.True(data.IsFavorite);
            }
        }
    }

    private void Link(Guid parentId, Guid childId, DbLinkedChildType type)
    {
        using var context = CreateContext();
        context.LinkedChildren.Add(new LinkedChildEntity { ParentId = parentId, ChildId = childId, ChildType = type });
        context.SaveChanges();
    }

    private void AssertForeignKeys()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        Assert.False(reader.Read());
    }

    private string[] Snapshot()
    {
        var tables = new List<string>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var rows = new List<string>();
        foreach (var table in tables)
        {
            using var command = _connection.CreateCommand();
#pragma warning disable CA2100 // Table names come only from this fixture's migrated SQLite schema.
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
#pragma warning restore CA2100
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(table + JsonSerializer.Serialize(values.Select(value => value is DBNull ? null : value)));
            }
        }

        return rows.ToArray();
    }

    private static BaseItemEntity Item(string name, Guid? parentId = null, Guid? ownerId = null, bool isFolder = false, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        Type = typeof(Movie).FullName!,
        ParentId = parentId,
        OwnerId = ownerId,
        IsFolder = isFolder
    };

    private static AncestorId Ancestor(Guid itemId, Guid parentId) => new()
    {
        ItemId = itemId,
        ParentItemId = parentId,
        Item = null!,
        ParentItem = null!
    };

    private static ItemValue Value(string value) => new()
    {
        ItemValueId = Guid.NewGuid(),
        Type = ItemValueType.Tags,
        Value = value,
        CleanValue = value
    };

    private static ItemValueMap ValueMap(Guid itemId, Guid valueId) => new()
    {
        ItemId = itemId,
        ItemValueId = valueId,
        Item = null!,
        ItemValue = null!
    };

    private static string Key(Guid id) => id.ToString("D", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();

    private TesserafinDbContext CreateContext() => new(
        _options,
        NullLogger<TesserafinDbContext>.Instance,
        new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
}
