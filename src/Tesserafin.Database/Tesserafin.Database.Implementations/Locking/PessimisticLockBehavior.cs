#pragma warning disable MT1013 // Releasing lock without guarantee of execution
#pragma warning disable MT1012 // Acquiring lock without guarantee of releasing
#pragma warning disable CA1873

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Tesserafin.Database.Implementations.Locking;

/// <summary>
/// A locking behavior that will always block any operation while a write is requested. Mimicks the old SqliteRepository behavior.
/// </summary>
/// <remarks>
/// The process-wide lock is owned by an EF Core connection, never by a thread: a transaction holds the
/// write lock from <c>BeginTransaction</c> until it is committed, rolled back or disposed, whichever
/// thread its asynchronous flow resumes on, and the commands and saves of the connection that owns
/// the write lock run inside it. Commands of every other connection wait for it.
/// </remarks>
public class PessimisticLockBehavior : IEntityFrameworkCoreLockingBehavior
{
    private readonly ILogger<PessimisticLockBehavior> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PessimisticLockBehavior"/> class.
    /// </summary>
    /// <param name="logger">The application logger.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public PessimisticLockBehavior(ILogger<PessimisticLockBehavior> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public void OnSaveChanges(TesserafinDbContext context, Action saveChanges)
    {
        var owner = context.GetService<IRelationalConnection>().ConnectionId;
        DatabaseLock.EnterWrite(_logger, owner, "SaveChanges");
        try
        {
            saveChanges();
        }
        finally
        {
            DatabaseLock.ExitWrite(_logger, owner);
        }
    }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder optionsBuilder)
    {
        _logger.LogInformation("The database locking mode has been set to: Pessimistic.");
        optionsBuilder.AddInterceptors(new CommandLockingInterceptor(_loggerFactory.CreateLogger<CommandLockingInterceptor>()));
        optionsBuilder.AddInterceptors(new TransactionLockingInterceptor(_loggerFactory.CreateLogger<TransactionLockingInterceptor>()));
        optionsBuilder.AddInterceptors(new ConnectionLockReleasingInterceptor(_loggerFactory.CreateLogger<ConnectionLockReleasingInterceptor>()));

        // EF Core raises no interceptor event when a transaction is disposed without a commit or a
        // rollback; its transaction object is the only place that knows.
        optionsBuilder.ReplaceService<IRelationalTransactionFactory, LockReleasingTransactionFactory>();
    }

    /// <inheritdoc/>
    public async Task OnSaveChangesAsync(TesserafinDbContext context, Func<Task> saveChanges)
    {
        var owner = context.GetService<IRelationalConnection>().ConnectionId;
        DatabaseLock.EnterWrite(_logger, owner, "SaveChanges");
        try
        {
            await saveChanges().ConfigureAwait(false);
        }
        finally
        {
            DatabaseLock.ExitWrite(_logger, owner);
        }
    }

    /// <summary>
    /// The write locks held by transactions: for each EF Core connection, the id of the transaction that
    /// holds its write lock. A transaction only ever releases its own, so an earlier transaction of the
    /// same connection that is disposed again cannot release the lock of the next one.
    /// </summary>
    private static class TransactionLocks
    {
        private static readonly ConcurrentDictionary<Guid, Guid> _holders = new();

        public static DbTransaction Begin(ILogger logger, DbConnection connection, TransactionStartingEventData eventData)
        {
            Acquire(logger, eventData.ConnectionId, eventData.TransactionId);
            try
            {
                return connection.BeginTransaction(eventData.IsolationLevel);
            }
            catch
            {
                Release(logger, eventData.ConnectionId, eventData.TransactionId);
                throw;
            }
        }

        public static async ValueTask<DbTransaction> BeginAsync(ILogger logger, DbConnection connection, TransactionStartingEventData eventData, CancellationToken cancellationToken)
        {
            Acquire(logger, eventData.ConnectionId, eventData.TransactionId);
            try
            {
                return await connection.BeginTransactionAsync(eventData.IsolationLevel, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Release(logger, eventData.ConnectionId, eventData.TransactionId);
                throw;
            }
        }

        /// <summary>
        /// Releases the write lock of a transaction, if it still holds it; every way a transaction ends
        /// calls this, so only the first call releases.
        /// </summary>
        public static void Release(ILogger logger, Guid connectionId, Guid transactionId)
        {
            if (_holders.TryRemove(new KeyValuePair<Guid, Guid>(connectionId, transactionId)))
            {
                DatabaseLock.ExitWrite(logger, connectionId);
            }
        }

        /// <summary>
        /// Releases the write lock of whichever transaction of a connection holds one: the transaction
        /// of a connection that closes ends with it.
        /// </summary>
        public static void ReleaseConnection(ILogger logger, Guid connectionId)
        {
            if (_holders.TryRemove(connectionId, out _))
            {
                DatabaseLock.ExitWrite(logger, connectionId);
            }
        }

        private static void Acquire(ILogger logger, Guid connectionId, Guid transactionId)
        {
            DatabaseLock.EnterWrite(logger, connectionId, "Transaction");
            if (!_holders.TryAdd(connectionId, transactionId))
            {
                // EF Core refuses a second transaction on a connection before raising the event; keep
                // the count balanced if that ever changes.
                DatabaseLock.ExitWrite(logger, connectionId);
            }
        }
    }

    private sealed class TransactionLockingInterceptor : DbTransactionInterceptor
    {
        private readonly ILogger _logger;

        public TransactionLockingInterceptor(ILogger logger)
        {
            _logger = logger;
        }

        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            return InterceptionResult<DbTransaction>.SuppressWithResult(TransactionLocks.Begin(_logger, connection, eventData));
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            return InterceptionResult<DbTransaction>.SuppressWithResult(await TransactionLocks.BeginAsync(_logger, connection, eventData, cancellationToken).ConfigureAwait(false));
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            TransactionLocks.Release(_logger, eventData.ConnectionId, eventData.TransactionId);

            base.TransactionCommitted(transaction, eventData);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            TransactionLocks.Release(_logger, eventData.ConnectionId, eventData.TransactionId);

            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }

        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
        {
            TransactionLocks.Release(_logger, eventData.ConnectionId, eventData.TransactionId);

            base.TransactionFailed(transaction, eventData);
        }

        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            TransactionLocks.Release(_logger, eventData.ConnectionId, eventData.TransactionId);

            return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        {
            TransactionLocks.Release(_logger, eventData.ConnectionId, eventData.TransactionId);

            base.TransactionRolledBack(transaction, eventData);
        }

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            TransactionLocks.Release(_logger, eventData.ConnectionId, eventData.TransactionId);

            return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
        }
    }

    /// <summary>
    /// Creates the EF Core transactions that release their write lock when they are disposed, after
    /// the underlying transaction rolled back.
    /// </summary>
    private sealed class LockReleasingTransactionFactory : RelationalTransactionFactory
    {
        public LockReleasingTransactionFactory(RelationalTransactionFactoryDependencies dependencies)
            : base(dependencies)
        {
        }

        public override RelationalTransaction Create(IRelationalConnection connection, DbTransaction transaction, Guid transactionId, IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned)
        {
            return new LockReleasingTransaction(connection, transaction, transactionId, logger, transactionOwned, Dependencies.SqlGenerationHelper);
        }
    }

    private sealed class LockReleasingTransaction : RelationalTransaction
    {
        private readonly Guid _connectionId;
        private readonly ILogger _logger;

        public LockReleasingTransaction(IRelationalConnection connection, DbTransaction transaction, Guid transactionId, IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger, bool transactionOwned, ISqlGenerationHelper sqlGenerationHelper)
            : base(connection, transaction, transactionId, logger, transactionOwned, sqlGenerationHelper)
        {
            _connectionId = connection.ConnectionId;
            _logger = logger.Logger;
        }

        public override void Dispose()
        {
            try
            {
                base.Dispose();
            }
            finally
            {
                TransactionLocks.Release(_logger, _connectionId, TransactionId);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                TransactionLocks.Release(_logger, _connectionId, TransactionId);
            }
        }
    }

    private sealed class ConnectionLockReleasingInterceptor : DbConnectionInterceptor
    {
        private readonly ILogger _logger;

        public ConnectionLockReleasingInterceptor(ILogger logger)
        {
            _logger = logger;
        }

        public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            TransactionLocks.ReleaseConnection(_logger, eventData.ConnectionId);

            return base.ConnectionClosing(connection, eventData, result);
        }

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            TransactionLocks.ReleaseConnection(_logger, eventData.ConnectionId);

            return base.ConnectionClosingAsync(connection, eventData, result);
        }

        public override InterceptionResult ConnectionDisposing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            TransactionLocks.ReleaseConnection(_logger, eventData.ConnectionId);

            return base.ConnectionDisposing(connection, eventData, result);
        }

        public override ValueTask<InterceptionResult> ConnectionDisposingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            TransactionLocks.ReleaseConnection(_logger, eventData.ConnectionId);

            return base.ConnectionDisposingAsync(connection, eventData, result);
        }
    }

    private sealed class CommandLockingInterceptor : DbCommandInterceptor
    {
        private readonly ILogger _logger;

        public CommandLockingInterceptor(ILogger logger)
        {
            _logger = logger;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            DatabaseLock.EnterWrite(_logger, eventData.ConnectionId, command.CommandText);
            try
            {
                return InterceptionResult<int>.SuppressWithResult(command.ExecuteNonQuery());
            }
            finally
            {
                DatabaseLock.ExitWrite(_logger, eventData.ConnectionId);
            }
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            DatabaseLock.EnterWrite(_logger, eventData.ConnectionId, command.CommandText);
            try
            {
                return InterceptionResult<int>.SuppressWithResult(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                DatabaseLock.ExitWrite(_logger, eventData.ConnectionId);
            }
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            var read = DatabaseLock.EnterRead(_logger, eventData.ConnectionId);
            try
            {
                return InterceptionResult<object>.SuppressWithResult(command.ExecuteScalar()!);
            }
            finally
            {
                DatabaseLock.ExitRead(_logger, read);
            }
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            var read = DatabaseLock.EnterRead(_logger, eventData.ConnectionId);
            try
            {
                return InterceptionResult<object>.SuppressWithResult((await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
            }
            finally
            {
                DatabaseLock.ExitRead(_logger, read);
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            var read = DatabaseLock.EnterRead(_logger, eventData.ConnectionId);
            try
            {
                return InterceptionResult<DbDataReader>.SuppressWithResult(command.ExecuteReader()!);
            }
            finally
            {
                DatabaseLock.ExitRead(_logger, read);
            }
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var read = DatabaseLock.EnterRead(_logger, eventData.ConnectionId);
            try
            {
                return InterceptionResult<DbDataReader>.SuppressWithResult(await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                DatabaseLock.ExitRead(_logger, read);
            }
        }
    }

    /// <summary>
    /// A process-wide reader/writer lock owned by EF Core connections instead of threads. The connection
    /// that holds the write lock can take it again (its transaction, then its saves and commands) and
    /// reads without waiting; every other connection waits for the write lock to be free, and writers
    /// waiting for the readers to finish keep new readers out.
    /// </summary>
    private static class DatabaseLock
    {
        private static readonly object _gate = new();
        private static Guid _writer;
        private static int _writerDepth;
        private static int _readers;
        private static int _waitingWriters;
        private static (string Operation, Guid Owner, DateTimeOffset Since) _holder;

        public static void EnterWrite(ILogger logger, Guid owner, string operation)
        {
            lock (_gate)
            {
                if (_writerDepth > 0 && _writer.Equals(owner))
                {
                    _writerDepth++;
                    return;
                }

                _waitingWriters++;
                try
                {
                    var congested = false;
                    while (_writerDepth > 0 || _readers > 0)
                    {
                        if (!Monitor.Wait(_gate, TimeSpan.FromSeconds(1)) && !congested)
                        {
                            congested = true;
                            logger.LogInformation("Query congestion detected: '{Owner}' since '{Date}' --- {Query}", _holder.Owner, _holder.Since, _holder.Operation);
                        }
                    }

                    if (congested)
                    {
                        logger.LogInformation("Query congestion cleared after {Duration}", DateTimeOffset.Now - _holder.Since);
                    }
                }
                finally
                {
                    _waitingWriters--;
                }

                _writer = owner;
                _writerDepth = 1;
                _holder = (operation, owner, DateTimeOffset.Now);
            }
        }

        public static void ExitWrite(ILogger logger, Guid owner)
        {
            lock (_gate)
            {
                if (_writerDepth == 0 || !_writer.Equals(owner))
                {
                    // A release by a connection that does not hold the lock would hand it to nobody.
                    logger.LogError("Database write lock released by connection {Owner}, which does not hold it.", owner);
                    throw new SynchronizationLockException("The database write lock is not held by connection " + owner + ".");
                }

                if (--_writerDepth == 0)
                {
                    _writer = Guid.Empty;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        /// <summary>
        /// Waits for the write lock of every other connection to be free.
        /// </summary>
        /// <returns><c>false</c> when the connection holds the write lock, so no read lock was taken.</returns>
        public static bool EnterRead(ILogger logger, Guid owner)
        {
            lock (_gate)
            {
                if (_writerDepth > 0 && _writer.Equals(owner))
                {
                    return false;
                }

                var congested = false;
                while (_writerDepth > 0 || _waitingWriters > 0)
                {
                    if (!Monitor.Wait(_gate, TimeSpan.FromSeconds(1)) && !congested)
                    {
                        congested = true;
                        logger.LogInformation("Read congestion detected: '{Owner}' since '{Date}' --- {Query}", _holder.Owner, _holder.Since, _holder.Operation);
                    }
                }

                _readers++;
                return true;
            }
        }

        public static void ExitRead(ILogger logger, bool taken)
        {
            if (!taken)
            {
                return;
            }

            lock (_gate)
            {
                if (_readers == 0)
                {
                    logger.LogError("Database read lock released while no read lock is held.");
                    throw new SynchronizationLockException("The database read lock is not held.");
                }

                if (--_readers == 0)
                {
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }
}
