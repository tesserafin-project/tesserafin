using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;

namespace Tesserafin.Database.Providers.Sqlite.Migrations
{
    /// <summary>
    /// The design time factory for <see cref="TesserafinDbContext"/>.
    /// This is only used for the creation of migrations and not during runtime.
    /// </summary>
    internal sealed class SqliteDesignTimeTesserafinDbFactory : IDesignTimeDbContextFactory<TesserafinDbContext>
    {
        public TesserafinDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<TesserafinDbContext>();
            optionsBuilder.UseSqlite("Data Source=reefin.db", f => f.MigrationsAssembly(GetType().Assembly));

            return new TesserafinDbContext(
                optionsBuilder.Options,
                NullLogger<TesserafinDbContext>.Instance,
                new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
                new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        }
    }
}
