using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reefin.Database.Implementations.Entities;

namespace Reefin.Database.Implementations.ModelConfiguration;

/// <summary>
/// FluentAPI configuration for the ActivityLog entity.
/// </summary>
public class ActivityLogConfiguration : IEntityTypeConfiguration<ActivityLog>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ActivityLog> builder)
    {
        builder.HasIndex(entity => entity.DateCreated);
    }
}
