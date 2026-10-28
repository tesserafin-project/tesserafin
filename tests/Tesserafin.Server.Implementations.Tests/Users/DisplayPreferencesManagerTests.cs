using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Entities;
using Tesserafin.Database.Implementations.Enums;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Server.Implementations.Users;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Users;

public sealed class DisplayPreferencesManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly IDbContextFactory<TesserafinDbContext> _factory;
    private readonly Guid _userId;
    private readonly Guid _otherUserId;

    public DisplayPreferencesManagerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite(_connection, options => options.MigrationsAssembly(typeof(SqliteDatabaseProvider).Assembly))
            .Options;

        using var context = CreateDbContext();
        context.Database.Migrate();
        var user = new User("viewer", "auth", "reset");
        var otherUser = new User("other-viewer", "auth", "reset");
        context.Users.AddRange(user, otherUser);
        context.SaveChanges();
        _userId = user.Id;
        _otherUserId = otherUser.Id;

        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(value => value.CreateDbContext()).Returns(CreateDbContext);
        _factory = factory.Object;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetItemDisplayPreferences_ReusesRequestedScopeAndReloadsSavedValues(bool globalScope)
    {
        // Non-empty ids can also name virtual display scopes, without a BaseItems row.
        var itemId = globalScope ? Guid.Empty : Guid.NewGuid();
        var manager = new DisplayPreferencesManager(_factory);
        var preferences = manager.GetItemDisplayPreferences(_userId, itemId, "web");
        Assert.Equal(itemId, preferences.ItemId);
        var originalId = preferences.Id;

        Assert.Equal(originalId, manager.GetItemDisplayPreferences(_userId, itemId, "web").Id);
        preferences.SortBy = "DateCreated";
        preferences.SortOrder = SortOrder.Descending;
        preferences.RememberSorting = true;
        manager.UpdateItemDisplayPreferences(preferences);

        var reloaded = new DisplayPreferencesManager(_factory).GetItemDisplayPreferences(_userId, itemId, "web");
        Assert.Equal(originalId, reloaded.Id);
        Assert.Equal(itemId, reloaded.ItemId);
        Assert.Equal("DateCreated", reloaded.SortBy);
        Assert.Equal(SortOrder.Descending, reloaded.SortOrder);
        Assert.True(reloaded.RememberSorting);

        using var context = CreateDbContext();
        Assert.Single(context.ItemDisplayPreferences);
        Assert.DoesNotContain(context.BaseItems, row => row.Id.Equals(itemId));
    }

    [Fact]
    public void GetItemDisplayPreferences_IsolatesUsersItemsAndClientsAndListsOnlyItemScopes()
    {
        var manager = new DisplayPreferencesManager(_factory);
        var firstItemId = Guid.NewGuid();
        var secondItemId = Guid.NewGuid();
        foreach (var userId in new[] { _userId, _otherUserId })
        {
            foreach (var client in new[] { "web", "tv" })
            {
                foreach (var itemId in new[] { Guid.Empty, firstItemId, secondItemId })
                {
                    var preferences = manager.GetItemDisplayPreferences(userId, itemId, client);
                    preferences.SortBy = preferences.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    manager.UpdateItemDisplayPreferences(preferences);
                }
            }
        }

        // Every combination has its own persisted identity and survives a fresh manager/context.
        using var context = CreateDbContext();
        var rows = context.ItemDisplayPreferences.AsNoTracking().ToArray();
        Assert.Equal(12, rows.Length);
        var reloadedManager = new DisplayPreferencesManager(_factory);
        foreach (var row in rows)
        {
            var reloaded = reloadedManager.GetItemDisplayPreferences(row.UserId, row.ItemId, row.Client);
            Assert.Equal(row.Id, reloaded.Id);
            Assert.Equal(row.SortBy, reloaded.SortBy);
        }

        foreach (var userId in new[] { _userId, _otherUserId })
        {
            foreach (var client in new[] { "web", "tv" })
            {
                var listed = reloadedManager.ListItemDisplayPreferences(userId, client);
                Assert.Equal(2, listed.Count);
                Assert.All(listed, row =>
                {
                    Assert.Equal(userId, row.UserId);
                    Assert.Equal(client, row.Client);
                });
                Assert.Contains(listed, row => row.ItemId.Equals(firstItemId));
                Assert.Contains(listed, row => row.ItemId.Equals(secondItemId));
            }
        }

        Assert.Equal(12, context.ItemDisplayPreferences.Count());
    }

    [Fact]
    public void GetItemDisplayPreferences_DoesNotReassignOrDeleteAmbiguousLegacyGlobalRows()
    {
        using (var context = CreateDbContext())
        {
            context.ItemDisplayPreferences.AddRange(
                new ItemDisplayPreferences(_userId, Guid.Empty, "web") { SortBy = "DateCreated" },
                new ItemDisplayPreferences(_userId, Guid.Empty, "web") { SortBy = "CommunityRating" });
            context.SaveChanges();
        }

        using var verificationContext = CreateDbContext();
        var originalRows = verificationContext.ItemDisplayPreferences.AsNoTracking().OrderBy(row => row.Id).ToArray();
        var itemId = Guid.NewGuid();
        var manager = new DisplayPreferencesManager(_factory);
        var preferences = manager.GetItemDisplayPreferences(_userId, itemId, "web");
        Assert.Equal(itemId, preferences.ItemId);
        Assert.Equal(preferences.Id, manager.GetItemDisplayPreferences(_userId, itemId, "web").Id);

        var remainingGlobalRows = verificationContext.ItemDisplayPreferences.AsNoTracking()
            .Where(row => row.ItemId.Equals(Guid.Empty)).OrderBy(row => row.Id).ToArray();
        Assert.Equal(originalRows.Select(row => row.Id), remainingGlobalRows.Select(row => row.Id));
        Assert.Equal(originalRows.Select(row => row.SortBy), remainingGlobalRows.Select(row => row.SortBy));
        Assert.Equal(3, verificationContext.ItemDisplayPreferences.Count());
        Assert.Equal(itemId, Assert.Single(manager.ListItemDisplayPreferences(_userId, "web")).ItemId);
    }

    [Fact]
    public void GetItemDisplayPreferences_ReadsAndUpdatesTheOldestOfLegacyDuplicates()
    {
        var itemId = Guid.NewGuid();
        using (var context = CreateDbContext())
        {
            context.ItemDisplayPreferences.AddRange(
                new ItemDisplayPreferences(_userId, itemId, "web") { SortBy = "Oldest" },
                new ItemDisplayPreferences(_userId, itemId, "web") { SortBy = "Newer" },
                new ItemDisplayPreferences(_userId, itemId, "web") { SortBy = "Newest" });
            context.SaveChanges();
        }

        using var verificationContext = CreateDbContext();
        var originalRows = verificationContext.ItemDisplayPreferences.AsNoTracking().OrderBy(row => row.Id).ToArray();
        var manager = new DisplayPreferencesManager(_factory);
        var preferences = manager.GetItemDisplayPreferences(_userId, itemId, "web");
        Assert.Equal(originalRows[0].Id, preferences.Id);

        preferences.SortBy = "Saved";
        manager.UpdateItemDisplayPreferences(preferences);
        Assert.Equal("Saved", new DisplayPreferencesManager(_factory).GetItemDisplayPreferences(_userId, itemId, "web").SortBy);

        // The other duplicates are neither merged nor deleted.
        var rows = verificationContext.ItemDisplayPreferences.AsNoTracking().OrderBy(row => row.Id).ToArray();
        Assert.Equal(originalRows.Select(row => row.Id), rows.Select(row => row.Id));
        Assert.Equal(new[] { "Saved", "Newer", "Newest" }, rows.Select(row => row.SortBy));
    }

    private TesserafinDbContext CreateDbContext()
    {
        return new TesserafinDbContext(
            _dbOptions,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
