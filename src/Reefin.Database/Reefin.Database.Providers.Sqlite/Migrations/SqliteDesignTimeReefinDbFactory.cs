using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;
using Reefin.Database.Implementations;
using Reefin.Database.Implementations.Locking;

namespace Reefin.Database.Providers.Sqlite.Migrations
{
    /// <summary>
    /// The design time factory for <see cref="ReefinDbContext"/>.
    /// This is only used for the creation of migrations and not during runtime.
    /// </summary>
    internal sealed class SqliteDesignTimeReefinDbFactory : IDesignTimeDbContextFactory<ReefinDbContext>
    {
        public ReefinDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<ReefinDbContext>();
            optionsBuilder.UseSqlite("Data Source=reefin.db", f => f.MigrationsAssembly(GetType().Assembly));

            return new ReefinDbContext(
                optionsBuilder.Options,
                NullLogger<ReefinDbContext>.Instance,
                new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
                new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        }
    }
}
