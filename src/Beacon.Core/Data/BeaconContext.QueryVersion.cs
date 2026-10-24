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
    protected static void ConfigureQueryVersionEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QueryVersion>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Label).HasMaxLength(200);
            entity.Property(e => e.StepsJson).IsRequired();
            entity.Property(e => e.CreatedByUserId).HasMaxLength(100);
            entity.Property(e => e.CreatedByUserName).HasMaxLength(200);
            entity.Property(e => e.ChangeSource).HasMaxLength(50);
            entity.Property(e => e.ChangeReason).HasMaxLength(2000);
            entity.Property(e => e.ContentHash).HasMaxLength(64);

            // Unique version number per query
            entity.HasIndex(e => new { e.QueryId, e.VersionNumber }).IsUnique();

            // Index for filtering by status
            entity.HasIndex(e => new { e.QueryId, e.Status });

            // Relationship with Query (cascade delete versions when query is deleted)
            entity.HasOne(e => e.Query)
                .WithMany(q => q.Versions)
                .HasForeignKey(e => e.QueryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Query.ActiveVersionId FK (self-referencing via QueryVersion)
        // SQL Server doesn't allow SetNull here due to the circular cascade path with QueryVersions->Queries.
        // ActiveVersionId must be nulled manually in application code before deleting a QueryVersion.
        modelBuilder.Entity<Query>(entity =>
        {
            entity.HasOne(q => q.ActiveVersion)
                .WithMany()
                .HasForeignKey(q => q.ActiveVersionId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}
