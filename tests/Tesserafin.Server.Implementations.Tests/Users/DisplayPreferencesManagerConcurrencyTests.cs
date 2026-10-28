using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>
/// Display preference writes against a migrated SQLite file, one connection per context as in
/// production, so that concurrent requests really interleave at the database.
/// </summary>
public sealed class DisplayPreferencesManagerConcurrencyTests : IDisposable
{
    private const int Writers = 8;
    private const int Rounds = 10;

    private readonly string _directory;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly IDbContextFactory<TesserafinDbContext> _factory;
    private readonly Guid _userId;

    public DisplayPreferencesManagerConcurrencyTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "tesserafin-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "tesserafin.db"),
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
        _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
            .UseSqlite(connectionString, options => options.MigrationsAssembly(typeof(SqliteDatabaseProvider).Assembly))
            .Options;

        using var context = CreateDbContext();
        context.Database.Migrate();
        var user = new User("viewer", "auth", "reset");
        context.Users.Add(user);
        context.SaveChanges();
        _userId = user.Id;

        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(value => value.CreateDbContext()).Returns(CreateDbContext);
        _factory = factory.Object;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void UpdateDisplayPreferences_ReplacesHomeSectionsInsteadOfAccumulatingThem()
    {
        var manager = new DisplayPreferencesManager(_factory);
        var itemId = Guid.NewGuid();

        // What DisplayPreferencesController.UpdateDisplayPreferences does on every save.
        foreach (var types in new[]
        {
            new[] { HomeSectionType.SmallLibraryTiles, HomeSectionType.Resume, HomeSectionType.NextUp },
            new[] { HomeSectionType.LatestMedia, HomeSectionType.None },
            new[] { HomeSectionType.LiveTv, HomeSectionType.ActiveRecordings, HomeSectionType.Resume },
        })
        {
            var preferences = manager.GetDisplayPreferences(_userId, itemId, "web");
            preferences.HomeSections.Clear();
            for (var order = 0; order < types.Length; order++)
            {
                preferences.HomeSections.Add(new HomeSection { Order = order, Type = types[order] });
            }

            manager.UpdateDisplayPreferences(preferences);
        }

        using var context = CreateDbContext();
        var stored = context.Set<HomeSection>().AsNoTracking().OrderBy(section => section.Order).ToArray();
        Assert.Equal(
            new[] { HomeSectionType.LiveTv, HomeSectionType.ActiveRecordings, HomeSectionType.Resume },
            stored.Select(section => section.Type));
        Assert.Equal(
            stored.Select(section => section.Order),
            new DisplayPreferencesManager(_factory).GetDisplayPreferences(_userId, itemId, "web")
                .HomeSections.Select(section => section.Order).OrderBy(order => order));
    }

    [Fact]
    public void UpdateDisplayPreferences_KeepsReloadedHomeSectionsThatAreNotCleared()
    {
        var manager = new DisplayPreferencesManager(_factory);
        var itemId = Guid.NewGuid();
        var preferences = manager.GetDisplayPreferences(_userId, itemId, "web");
        preferences.HomeSections.Add(new HomeSection { Order = 0, Type = HomeSectionType.Resume });
        preferences.HomeSections.Add(new HomeSection { Order = 1, Type = HomeSectionType.NextUp });
        manager.UpdateDisplayPreferences(preferences);

        // A caller that edits loaded sections in place, rather than replacing them, must not lose them.
        var reloaded = manager.GetDisplayPreferences(_userId, itemId, "web");
        var kept = reloaded.HomeSections.Single(section => section.Order == 0);
        kept.Type = HomeSectionType.LatestMedia;
        reloaded.HomeSections.Remove(reloaded.HomeSections.Single(section => section.Order == 1));
        manager.UpdateDisplayPreferences(reloaded);

        using var context = CreateDbContext();
        var stored = Assert.Single(context.Set<HomeSection>().AsNoTracking());
        Assert.Equal(kept.Id, stored.Id);
        Assert.Equal(HomeSectionType.LatestMedia, stored.Type);
    }

    [Fact]
    public async Task UpdateDisplayPreferences_ConcurrentSavesLeaveOneCompleteSetOfHomeSections()
    {
        var types = Enum.GetValues<HomeSectionType>();
        Assert.True(types.Length > Writers);
        for (var round = 0; round < Rounds; round++)
        {
            var itemId = Guid.NewGuid();
            new DisplayPreferencesManager(_factory).GetDisplayPreferences(_userId, itemId, "web");
            var writer = 0;
            await RunConcurrently(manager =>
            {
                // Writer k saves k sections, all of type k, so any mix of two saves is visible.
                var self = Interlocked.Increment(ref writer);
                var preferences = manager.GetDisplayPreferences(_userId, itemId, "web");
                preferences.HomeSections.Clear();
                for (var order = 0; order < self; order++)
                {
                    preferences.HomeSections.Add(new HomeSection { Order = order, Type = types[self] });
                }

                manager.UpdateDisplayPreferences(preferences);
                return self;
            });

            var sections = new DisplayPreferencesManager(_factory).GetDisplayPreferences(_userId, itemId, "web").HomeSections;
            Assert.Single(sections.Select(section => section.Type).Distinct());
            var winner = Array.IndexOf(types, sections.First().Type);
            Assert.Equal(Enumerable.Range(0, winner), sections.Select(section => section.Order).OrderBy(order => order));
        }
    }

    [Fact]
    public async Task GetDisplayPreferences_ConcurrentFirstReadsShareOneRow()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var itemId = Guid.NewGuid();
            var ids = await RunConcurrently(manager => manager.GetDisplayPreferences(_userId, itemId, "web").Id);

            Assert.Single(ids.Distinct());
            using var context = CreateDbContext();
            Assert.Equal(1, context.DisplayPreferences.Count(row => row.ItemId.Equals(itemId)));
        }
    }

    [Fact]
    public async Task GetItemDisplayPreferences_ConcurrentFirstReadsCreateNoDuplicates()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var itemId = Guid.NewGuid();
            var ids = await RunConcurrently(manager => manager.GetItemDisplayPreferences(_userId, itemId, "web").Id);

            Assert.Single(ids.Distinct());
            using var context = CreateDbContext();
            Assert.Equal(1, context.ItemDisplayPreferences.Count(row => row.ItemId.Equals(itemId)));
        }
    }

    [Fact]
    public async Task SetCustomItemDisplayPreferences_ConcurrentSavesLeaveExactlyOneCompleteSet()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var itemId = Guid.NewGuid();
            var writer = 0;
            await RunConcurrently(manager =>
            {
                // Each request sends its own complete set: shared keys, plus keys only it knows.
                var self = Interlocked.Increment(ref writer);
                var values = new Dictionary<string, string?>
                {
                    ["shared"] = "writer-" + self,
                    ["only-" + self] = "writer-" + self,
                };
                manager.SetCustomItemDisplayPreferences(_userId, itemId, "web", values);
                return self;
            });

            var stored = new DisplayPreferencesManager(_factory).ListCustomItemDisplayPreferences(_userId, itemId, "web");
            Assert.Equal(2, stored.Count);
            var winner = Assert.Single(stored.Keys, key => key.StartsWith("only-", StringComparison.Ordinal));
            Assert.Equal("writer-" + winner["only-".Length..], stored["shared"]);
            Assert.Equal(stored["shared"], stored[winner]);
        }
    }

    private async Task<T[]> RunConcurrently<T>(Func<DisplayPreferencesManager, T> action)
    {
        // One manager, as the singleton registration in CoreAppHost; one context per call.
        var manager = new DisplayPreferencesManager(_factory);
        using var start = new Barrier(Writers);
        var tasks = Enumerable.Range(0, Writers)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    return action(manager);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        return await Task.WhenAll(tasks);
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
