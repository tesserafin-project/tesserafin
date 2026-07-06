using Microsoft.EntityFrameworkCore;
using Reefin.Database.Providers.Sqlite.Migrations;
using Reefin.Server.Implementations.Migrations;
using Xunit;

namespace Reefin.Server.Implementations.Tests.EfMigrations;

public class EfMigrationTests
{
    [Fact]
    public void CheckForUnappliedMigrations_SqLite()
    {
        var dbDesignContext = new SqliteDesignTimeReefinDbFactory();
        var context = dbDesignContext.CreateDbContext([]);
        Assert.False(context.Database.HasPendingModelChanges(), "There are unapplied changes to the EFCore model for SQLite. Please create a Migration.");
    }
}
