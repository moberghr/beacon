using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Entities.RowSecurity;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using System.Linq.Expressions;

namespace Beacon.Core.Data;

public abstract partial class BeaconContext
{
    protected static void ConfigureCommentEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Content)
                  .HasMaxLength(4000)
                  .IsRequired();

            entity.Property(e => e.UserId)
                  .HasMaxLength(100);

            entity.Property(e => e.UserName)
                  .HasMaxLength(200);

            // Composite index for efficient lookup by entity
            entity.HasIndex(e => new { e.EntityType, e.EntityId });

            // Index for ordering by creation time
            entity.HasIndex(e => e.CreatedTime);
        });
    }
}
