using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tesserafin.Database.Implementations;

namespace Tesserafin.Server.HealthChecks;

/// <summary>
/// The production <see cref="IDatabaseHealthProbe"/>: executes one real statement against the
/// application database over the Entity Framework connection.
/// </summary>
/// <remarks>
/// The statement is <c>SELECT 1</c> — the cheapest round trip that still proves the file could be
/// opened, the connection is usable and the engine answers. Checking that the database file merely
/// exists is deliberately NOT what this does: a present-but-locked, present-but-corrupt or
/// present-but-unreadable database would pass such a check and fail every real request.
/// </remarks>
public sealed class DatabaseHealthProbe : IDatabaseHealthProbe
{
    private readonly IDbContextFactory<TesserafinDbContext> _dbContextFactory;
    private readonly ILogger<DatabaseHealthProbe> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseHealthProbe"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="logger">The logger.</param>
    public DatabaseHealthProbe(IDbContextFactory<TesserafinDbContext> dbContextFactory, ILogger<DatabaseHealthProbe> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var connection = dbContext.Database.GetDbConnection();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 1";
                    var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return scalar is not null
                           && Convert.ToInt64(scalar, CultureInfo.InvariantCulture) == 1L;
                }
                finally
                {
                    await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // Any failure to answer SELECT 1 is, for the purposes of this endpoint, an unreachable
            // database. The reason is logged (structured, with the exception object) and is
            // deliberately never propagated to the HTTP response, which would leak the database
            // path, the connection string or a stack trace to an unauthenticated caller.
            _logger.LogWarning(ex, "Database health probe failed");
            return false;
        }
    }
}
