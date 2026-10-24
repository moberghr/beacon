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
    protected static void ConfigureApprovalEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QueryApprovalRequest>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.RequestedByUserId).HasMaxLength(100);
            entity.Property(e => e.RequestedByUserName).HasMaxLength(200);
            entity.Property(e => e.ReviewedByUserId).HasMaxLength(100);
            entity.Property(e => e.ReviewedByUserName).HasMaxLength(200);
            entity.Property(e => e.ReviewComment).HasMaxLength(2000);
            entity.Property(e => e.ChangeSummary).HasMaxLength(2000);
            entity.Property(e => e.ApprovedContentHash).HasMaxLength(64);

            // Indexes
            entity.HasIndex(e => new { e.QueryId, e.Status });
            entity.HasIndex(e => new { e.Status, e.CreatedTime });
            entity.HasIndex(e => e.Status);

            // Relationships
            entity.HasOne(e => e.Query)
                .WithMany()
                .HasForeignKey(e => e.QueryId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction to avoid cascade cycle through Query -> QueryVersion -> QueryApprovalRequest
            entity.HasOne(e => e.QueryVersion)
                .WithMany()
                .HasForeignKey(e => e.QueryVersionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // A change of another governed resource: the resource is named by kind and id, not a key, so a request outlives the
        // resource it was about.
        modelBuilder.Entity<ChangeApprovalRequest>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ResourceName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.DefinitionHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProposedJson).IsRequired();
            entity.Property(e => e.RequestedByUserId).HasMaxLength(100);
            entity.Property(e => e.RequestedByUserName).HasMaxLength(200);
            entity.Property(e => e.ReviewedByUserId).HasMaxLength(100);
            entity.Property(e => e.ReviewedByUserName).HasMaxLength(200);
            entity.Property(e => e.ReviewComment).HasMaxLength(2000);
            entity.Property(e => e.ChangeSummary).HasMaxLength(2000);

            entity.HasIndex(e => new { e.Kind, e.ResourceId, e.Status });
            entity.HasIndex(e => new { e.Status, e.CreatedTime });
        });

        // The approved definition each governed resource is checked against (null: not governed).
        modelBuilder.Entity<MigrationJob>()
            .Property(e => e.ApprovedDefinitionHash)
            .HasMaxLength(64);

        modelBuilder.Entity<DataContractRule>()
            .Property(e => e.ApprovedSqlHash)
            .HasMaxLength(64);

        modelBuilder.Entity<Recipient>()
            .Property(e => e.ApprovedDestinationHash)
            .HasMaxLength(64);

        modelBuilder.Entity<DataSource>()
            .Property(e => e.ApprovedTargetHash)
            .HasMaxLength(100);
    }
}
