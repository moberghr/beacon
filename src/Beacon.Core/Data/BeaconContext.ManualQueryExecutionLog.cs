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
    protected static void ConfigureManualQueryExecutionLogEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManualQueryExecutionLog>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.UserId).HasMaxLength(100);
            entity.Property(e => e.QueryText).IsRequired();
            entity.Property(e => e.ExecutionContext).HasMaxLength(100);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);
            entity.Property(e => e.HmacKeyId).HasMaxLength(32);
            entity.Property(e => e.Hmac).HasMaxLength(64);

            // Relationship with DataSource
            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes for querying
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => e.CreatedTime);
            entity.HasIndex(e => e.ExecutionContext);
            entity.HasIndex(e => new { e.UserId, e.CreatedTime });
            entity.HasIndex(e => new { e.DataSourceId, e.CreatedTime });
            entity.HasIndex(e => new { e.TriggeredByUserId, e.CreatedTime });
            entity.HasIndex(e => e.AuditGuid).IsUnique();
        });
    }
}
