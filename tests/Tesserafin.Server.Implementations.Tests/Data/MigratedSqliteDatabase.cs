using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.DbConfiguration;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;

namespace Tesserafin.Server.Implementations.Tests.Data;

/// <summary>
/// A SQLite file in a directory of its own, created by the EF Core migrations and configured by the
/// production provider (pooled connections, WAL, one connection per context) under a locking mode.
/// </summary>
public sealed class MigratedSqliteDatabase : IDisposable
{
    private readonly string _directory;
    private readonly IEntityFrameworkCoreLockingBehavior _locking;
    private readonly DbContextOptions<TesserafinDbContext> _dbOptions;

    public MigratedSqliteDatabase(IEntityFrameworkCoreLockingBehavior locking)
    {
        _locking = locking;
        _directory = Path.Combine(Path.GetTempPath(), "tesserafin-db-" + Guid.NewGuid().ToString("N"));
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
        var unlocked = builder.Options;
        locking.Initialise(builder);
        _dbOptions = builder.Options;

        // Migrated without the mode under test, so that a lock an earlier test leaked fails that test's
        // deadline instead of hanging the construction of the next one.
        using var context = new TesserafinDbContext(
            unlocked,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        context.Database.Migrate();
    }

    /// <summary>
    /// Gets the deadline of a scenario that a leaked lock would block.
    /// </summary>
    public static TimeSpan Deadline { get; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Opens a context, on a connection of its own, under the locking mode.
    /// </summary>
    /// <param name="interceptors">Interceptors added to this context only.</param>
    /// <returns>The context.</returns>
    public TesserafinDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        var options = interceptors.Length == 0
            ? _dbOptions
            : new DbContextOptionsBuilder<TesserafinDbContext>(_dbOptions).AddInterceptors(interceptors).Options;
        return new TesserafinDbContext(
            options,
            NullLogger<TesserafinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            _locking);
    }

    /// <summary>
    /// A context factory as the server registers it, one context per call.
    /// </summary>
    /// <param name="interceptors">Interceptors added to every context it creates.</param>
    /// <returns>The factory.</returns>
    public IDbContextFactory<TesserafinDbContext> Factory(params IInterceptor[] interceptors)
    {
        var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
        factory.Setup(value => value.CreateDbContext()).Returns(() => CreateDbContext(interceptors));
        factory.Setup(value => value.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => CreateDbContext(interceptors));
        return factory.Object;
    }

    /// <summary>
    /// Runs an action on a thread of its own and waits for it under <see cref="Deadline"/>: a lock that
    /// is reentrant for the thread that holds it would hide a leak from the test thread.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns>The exception it threw, if any; a timeout when it is still blocked.</returns>
    public static Exception? RunOnAnotherThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        return thread.Join(Deadline) ? error : new TimeoutException("Still blocked after " + Deadline + ".");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    /// <summary>
    /// Fails every save of the contexts it is added to, after any statement the caller ran before it.
    /// </summary>
    internal sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        private int _failures;

        public int Failures => _failures;

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _failures);
            throw new InvalidOperationException("Injected save failure.");
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _failures);
            throw new InvalidOperationException("Injected save failure.");
        }
    }
}
