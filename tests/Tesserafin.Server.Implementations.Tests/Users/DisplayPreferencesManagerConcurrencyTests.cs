using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Extensions;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.DbConfiguration;
using Tesserafin.Database.Implementations.Entities;
using Tesserafin.Database.Implementations.Enums;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Server.Implementations.Users;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Users;

/// <summary>
/// Display preference writes against a migrated SQLite file, configured by the production provider
/// (pooled connections, WAL, one connection per context), so that concurrent requests really
/// interleave at the database. Runs once per database locking mode an administrator can select.
/// </summary>
public abstract class DisplayPreferencesManagerConcurrencyTests : IDisposable
{
    private const int Writers = 8;
    private const int Rounds = 10;

    private readonly string _directory;
    private readonly IEntityFrameworkCoreLockingBehavior _locking;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
    private readonly IDbContextFactory<TesserafinDbContext> _factory;
    private readonly Guid _userId;

    protected DisplayPreferencesManagerConcurrencyTests(IEntityFrameworkCoreLockingBehavior locking)
    {
        _locking = locking;
        _directory = Path.Combine(Path.GetTempPath(), "tesserafin-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var builder = new DbContextOptionsBuilder<TesserafinDbContext>();
        new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance).Initialise(
            builder,
            new DatabaseConfigurationOptions
            {
                DatabaseType = "Tesserafin-SQLite",
                LockingBehavior = DatabaseLockingBehaviorTypes.NoLock,
                CustomProviderOptions = new CustomDatabaseOptions
                {
                    PluginName = "Tesserafin-SQLite",
                    PluginAssembly = string.Empty,
                    ConnectionString = string.Empty,
                    Options = { new CustomDatabaseOption { Key = "path", Value = Path.Combine(_directory, "tesserafin.db") } },
                },
            });
        locking.Initialise(builder);
        _dbOptions = builder.Options;

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
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
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

    [Fact]
    public async Task GetPreferences_ConcurrentFirstReadsAcrossScopesCreateOneRowPerScope()
    {
        for (var round = 0; round < Rounds; round++)
        {
            // Two users, two clients, and the three kinds of scope the API sends: the global scope,
            // a virtual scope named by the client (hashed as the controller does) and an item id.
            var scopes = (
                from userId in new[] { AddUser(), AddUser() }
                from client in new[] { "web", "android" }
                from itemId in new[] { Guid.Empty, "usersettings".GetMD5(), Guid.NewGuid() }
                select (UserId: userId, ItemId: itemId, Client: client)).ToArray();

            var writer = 0;
            var reads = await RunConcurrently(manager =>
            {
                // Every writer reads every scope, each starting from a different one.
                var self = Interlocked.Increment(ref writer);
                return scopes
                    .Select((_, index) => scopes[(index + self) % scopes.Length])
                    .Select(scope => (
                        Scope: scope,
                        Display: manager.GetDisplayPreferences(scope.UserId, scope.ItemId, scope.Client).Id,
                        Item: manager.GetItemDisplayPreferences(scope.UserId, scope.ItemId, scope.Client).Id))
                    .ToArray();
            });

            using var context = CreateDbContext();
            foreach (var scope in scopes)
            {
                var seen = reads.SelectMany(read => read).Where(read => read.Scope == scope).ToArray();
                Assert.Equal(Writers, seen.Length);
                var display = Assert.Single(context.DisplayPreferences.AsNoTracking().Where(row =>
                    row.UserId.Equals(scope.UserId) && row.ItemId.Equals(scope.ItemId) && row.Client == scope.Client));
                var item = Assert.Single(context.ItemDisplayPreferences.AsNoTracking().Where(row =>
                    row.UserId.Equals(scope.UserId) && row.ItemId.Equals(scope.ItemId) && row.Client == scope.Client));
                Assert.All(seen, read => Assert.Equal(display.Id, read.Display));
                Assert.All(seen, read => Assert.Equal(item.Id, read.Item));
            }
        }
    }

    [Fact]
    public async Task UpdateItemDisplayPreferences_ConcurrentScopesKeepTheirOwnValuesAfterReopening()
    {
        var users = new[] { AddUser(), AddUser() };
        var scopes = (
            from userId in users
            from client in new[] { "web", "android" }
            from itemId in new[] { Guid.Empty, "usersettings".GetMD5(), Guid.NewGuid(), Guid.NewGuid() }
            select (UserId: userId, ItemId: itemId, Client: client)).ToArray();
        Assert.Equal(0, scopes.Length % Writers);

        var writer = 0;
        await RunConcurrently(manager =>
        {
            // Writer k owns every Writers-th scope and stamps it with the scope's own index.
            var self = Interlocked.Increment(ref writer) - 1;
            for (var index = self; index < scopes.Length; index += Writers)
            {
                var (userId, itemId, client) = scopes[index];
                var preferences = manager.GetItemDisplayPreferences(userId, itemId, client);
                preferences.SortBy = "scope-" + index;
                preferences.SortOrder = index % 2 == 0 ? SortOrder.Ascending : SortOrder.Descending;
                preferences.RememberSorting = true;
                manager.UpdateItemDisplayPreferences(preferences);
            }

            return self;
        });

        // Close every pooled connection: this read opens a new connection to the database file,
        // through a new manager.
        SqliteConnection.ClearAllPools();
        var reopened = new DisplayPreferencesManager(_factory);
        for (var index = 0; index < scopes.Length; index++)
        {
            var (userId, itemId, client) = scopes[index];
            var stored = reopened.GetItemDisplayPreferences(userId, itemId, client);
            Assert.Equal("scope-" + index, stored.SortBy);
            Assert.Equal(index % 2 == 0 ? SortOrder.Ascending : SortOrder.Descending, stored.SortOrder);
            Assert.True(stored.RememberSorting);
        }

        using var context = CreateDbContext();
        Assert.Equal(scopes.Length, context.ItemDisplayPreferences.Count(row => users.Contains(row.UserId)));
    }

    [Fact]
    public void SetCustomItemDisplayPreferences_FailedSaveKeepsThePreviousSet()
    {
        var itemId = Guid.NewGuid();
        new DisplayPreferencesManager(_factory).SetCustomItemDisplayPreferences(
            _userId,
            itemId,
            "web",
            new Dictionary<string, string?> { ["kept"] = "before", ["also-kept"] = "before" });

        // The delete of the previous set is executed before the insert of the new one fails.
        var failing = new FailingSaveInterceptor();
        Assert.Throws<InvalidOperationException>(() => new DisplayPreferencesManager(FactoryWith(failing))
            .SetCustomItemDisplayPreferences(_userId, itemId, "web", new Dictionary<string, string?> { ["new"] = "after" }));
        Assert.Equal(1, failing.Failures);

        var stored = new DisplayPreferencesManager(_factory).ListCustomItemDisplayPreferences(_userId, itemId, "web");
        Assert.Equal(
            new Dictionary<string, string?> { ["kept"] = "before", ["also-kept"] = "before" },
            stored);
    }

    [Fact]
    public void UpdateDisplayPreferences_FailedSaveKeepsThePreviousHomeSections()
    {
        var manager = new DisplayPreferencesManager(_factory);
        var itemId = Guid.NewGuid();
        var preferences = manager.GetDisplayPreferences(_userId, itemId, "web");
        preferences.HomeSections.Add(new HomeSection { Order = 0, Type = HomeSectionType.Resume });
        preferences.HomeSections.Add(new HomeSection { Order = 1, Type = HomeSectionType.NextUp });
        manager.UpdateDisplayPreferences(preferences);

        // The delete of the cleared sections is executed before the save of the new ones fails.
        var replacement = manager.GetDisplayPreferences(_userId, itemId, "web");
        replacement.HomeSections.Clear();
        replacement.HomeSections.Add(new HomeSection { Order = 0, Type = HomeSectionType.LiveTv });
        var failing = new FailingSaveInterceptor();
        Assert.Throws<InvalidOperationException>(() => new DisplayPreferencesManager(FactoryWith(failing))
            .UpdateDisplayPreferences(replacement));
        Assert.Equal(1, failing.Failures);

        using var context = CreateDbContext();
        Assert.Equal(
            new[] { HomeSectionType.Resume, HomeSectionType.NextUp },
            context.Set<HomeSection>().AsNoTracking().OrderBy(section => section.Order).Select(section => section.Type));
    }

    [Fact]
    public async Task LegacyDuplicateScopes_AreClassifiedAndLeftUntouchedByConcurrentReads()
    {
        // Rows as the getter before #362 and the get-or-create race left them. Nothing records
        // which item a global row was requested for.
        var otherUser = AddUser();
        var identical = Guid.NewGuid();
        var contradictory = Guid.NewGuid();
        using (var seed = CreateDbContext())
        {
            seed.ItemDisplayPreferences.AddRange(
                new ItemDisplayPreferences(_userId, identical, "web"),
                new ItemDisplayPreferences(_userId, identical, "web"),
                new ItemDisplayPreferences(_userId, contradictory, "web"),
                new ItemDisplayPreferences(_userId, contradictory, "web") { SortBy = "DateCreated", SortOrder = SortOrder.Descending, RememberSorting = true },
                new ItemDisplayPreferences(_userId, Guid.Empty, "web"),
                new ItemDisplayPreferences(_userId, Guid.Empty, "web"),
                new ItemDisplayPreferences(otherUser, Guid.Empty, "web"),
                new ItemDisplayPreferences(otherUser, Guid.Empty, "web") { SortBy = "PremiereDate", RememberSorting = true },
                new ItemDisplayPreferences(otherUser, Guid.Empty, "android"));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var before = SnapshotItemDisplayPreferences();
        var groups = DiagnoseDuplicateItemDisplayPreferences();
        Assert.Equal(4, groups.Count);
        Assert.Equal((2, 1, 2, false), groups[(_userId, identical, "web")]);
        Assert.Equal((2, 2, 1, false), groups[(_userId, contradictory, "web")]);
        Assert.Equal((2, 1, 2, true), groups[(_userId, Guid.Empty, "web")]);
        Assert.Equal((2, 2, 1, true), groups[(otherUser, Guid.Empty, "web")]);
        Assert.DoesNotContain((otherUser, Guid.Empty, "android"), groups.Keys);

        var reads = await RunConcurrently(manager => groups.Keys
            .Select(scope => manager.GetItemDisplayPreferences(scope.UserId, scope.ItemId, scope.Client).Id)
            .ToArray());

        // Every reader sees the oldest row of each scope; no row is added, merged or deleted.
        int[] oldest;
        using (var context = CreateDbContext())
        {
            oldest = groups.Keys.Select(scope => context.ItemDisplayPreferences
                .Where(row => row.UserId.Equals(scope.UserId) && row.ItemId.Equals(scope.ItemId) && row.Client == scope.Client)
                .Min(row => row.Id)).ToArray();
        }

        Assert.All(reads, read => Assert.Equal(oldest, read));
        Assert.Equal(before, SnapshotItemDisplayPreferences());
        Assert.Equal(groups, DiagnoseDuplicateItemDisplayPreferences());
    }

    private Dictionary<(Guid UserId, Guid ItemId, string Client), (long Rows, long DistinctSettings, long DefaultRows, bool Global)> DiagnoseDuplicateItemDisplayPreferences()
    {
        // Read-only query, kept in step with docs/data-model-reliability.md.
        const string Diagnostic = """
            SELECT UserId, ItemId, Client,
                   COUNT(*) AS Rows,
                   COUNT(DISTINCT SortBy || '|' || SortOrder || '|' || ViewType || '|' || IFNULL(IndexBy, '-')
                       || '|' || RememberSorting || '|' || RememberIndexing) AS DistinctSettings,
                   SUM(SortBy = 'SortName' AND SortOrder = 0 AND ViewType = 0 AND IndexBy IS NULL
                       AND RememberSorting = 0 AND RememberIndexing = 0) AS DefaultRows,
                   ItemId = '00000000-0000-0000-0000-000000000000' AS GlobalScope,
                   MIN(Id) AS ReadRowId
            FROM ItemDisplayPreferences
            GROUP BY UserId, ItemId, Client
            HAVING COUNT(*) > 1
            ORDER BY UserId, ItemId, Client;
            """;

        using var context = CreateDbContext();
        using var connection = new SqliteConnection(context.Database.GetConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Diagnostic;
        using var reader = command.ExecuteReader();
        var groups = new Dictionary<(Guid, Guid, string), (long, long, long, bool)>();
        while (reader.Read())
        {
            groups.Add(
                (reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)),
                (reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetBoolean(6)));
        }

        return groups;
    }

    private string[] SnapshotItemDisplayPreferences()
    {
        using var context = CreateDbContext();
        return context.ItemDisplayPreferences.AsNoTracking().OrderBy(row => row.Id).AsEnumerable()
            .Select(row => string.Join(
                '|',
                row.Id,
                row.UserId,
                row.ItemId,
                row.Client,
                row.SortBy,
                row.SortOrder,
                row.ViewType,
                row.IndexBy,
                row.RememberSorting,
                row.RememberIndexing))
            .ToArray();
    }

    private Guid AddUser()
    {
        using var context = CreateDbContext();
        var user = new User("viewer-" + Guid.NewGuid().ToString("N"), "auth", "reset");
        context.Users.Add(user);
        context.SaveChanges();
        return user.Id;
    }

    private IDbContextFactory<TesserafinDbContext> FactoryWith(IInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<TesserafinDbContext>(_dbOptions).AddInterceptors(interceptor).Options;
        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(value => value.CreateDbContext()).Returns(() => CreateDbContext(options));
        return factory.Object;
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

    private TesserafinDbContext CreateDbContext() => CreateDbContext(_dbOptions);

    private TesserafinDbContext CreateDbContext(DbContextOptions<TesserafinDbContext> options)
    {
        return new TesserafinDbContext(
            options,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            _locking);
    }

    // Nested as in LockingBehaviorSaveFailureTests: each mode only passes its locking behavior.
#pragma warning disable CA1034 // Nested types should not be visible

    /// <summary>
    /// The default mode.
    /// </summary>
    public sealed class NoLock : DisplayPreferencesManagerConcurrencyTests
    {
        public NoLock()
            : base(new NoLockBehavior(NullLogger<NoLockBehavior>.Instance))
        {
        }
    }

    /// <summary>
    /// A process-wide reader/writer lock around commands and transactions.
    /// </summary>
    public sealed class Pessimistic : DisplayPreferencesManagerConcurrencyTests
    {
        public Pessimistic()
            : base(new PessimisticLockBehavior(NullLogger<PessimisticLockBehavior>.Instance, NullLoggerFactory.Instance))
        {
        }
    }

    /// <summary>
    /// Retries of commands and transactions that find the database locked.
    /// </summary>
    public sealed class Optimistic : DisplayPreferencesManagerConcurrencyTests
    {
        public Optimistic()
            : base(new OptimisticLockBehavior(NullLogger<OptimisticLockBehavior>.Instance))
        {
        }
    }

#pragma warning restore CA1034

    /// <summary>
    /// Fails the first save of the context, after any statement the caller ran before it.
    /// </summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public int Failures { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Failures++;
            throw new InvalidOperationException("Injected save failure.");
        }
    }
}
