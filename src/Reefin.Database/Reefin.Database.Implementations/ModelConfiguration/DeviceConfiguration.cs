using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reefin.Database.Implementations.Entities.Security;

namespace Reefin.Database.Implementations.ModelConfiguration
{
    /// <summary>
    /// FluentAPI configuration for the Device entity.
    /// </summary>
    public class DeviceConfiguration : IEntityTypeConfiguration<Device>
    {
        /// <inheritdoc/>
        public void Configure(EntityTypeBuilder<Device> builder)
        {
            builder
                .HasIndex(entity => new { entity.DeviceId, entity.DateLastActivity });

            builder
                .HasIndex(entity => new { entity.AccessToken, entity.DateLastActivity });

            builder
                .HasIndex(entity => new { entity.UserId, entity.DeviceId });
        }
    }
}
