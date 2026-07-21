using Microsoft.EntityFrameworkCore;
using Tesserafin.Database.Providers.Sqlite.Migrations;
using Tesserafin.Server.Implementations.Migrations;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.EfMigrations;

public class EfMigrationTests
{
    [Fact]
    public void CheckForUnappliedMigrations_SqLite()
    {
        var dbDesignContext = new SqliteDesignTimeTesserafinDbFactory();
        var context = dbDesignContext.CreateDbContext([]);
        Assert.False(context.Database.HasPendingModelChanges(), "There are unapplied changes to the EFCore model for SQLite. Please create a Migration.");
    }
}
