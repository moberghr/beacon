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
    protected static void ConfigureAiActorEntities(ModelBuilder modelBuilder)
    {
        // AiActor configuration
        modelBuilder.Entity<AiActor>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Instructions).IsRequired();
            entity.Property(e => e.CreatedByUserId).HasMaxLength(100);
            entity.Property(e => e.LastError).HasMaxLength(4000);

            // Indexes
            // Note: Don't add explicit HasIndex for DataSourceId - EF Core creates one automatically for the FK
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ArchivedTime);
            entity.HasIndex(e => new { e.DataSourceId, e.Status });
            entity.HasIndex(e => new { e.Status, e.ArchivedTime });

            // Relationships
            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.Executions)
                .WithOne(e => e.AiActor)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Conversations)
                .WithOne(e => e.AiActor)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Queries)
                .WithOne(e => e.AiActor)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.Subscriptions)
                .WithOne(e => e.AiActor)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // AiActorExecution configuration
        modelBuilder.Entity<AiActorExecution>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.DecisionSummary).HasMaxLength(4000);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);

            // Indexes
            // Note: Don't add explicit HasIndex for FK columns - EF Core creates indexes automatically for FKs
            // AiActorId index is created by parent relationship (AiActor.Executions)
            entity.HasIndex(e => e.Phase);
            entity.HasIndex(e => e.StartedAt);
            entity.HasIndex(e => new { e.AiActorId, e.StartedAt });

            // Relationships
            entity.HasOne(e => e.TriggeringSubscription)
                .WithMany()
                .HasForeignKey(e => e.TriggeringSubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);

            // NoAction prevents SQL Server cascade cycle (AiActor -> AiActorExecution and AiActor -> AiActorPlan -> AiActorExecution)
            entity.HasOne(e => e.AiActorPlan)
                .WithOne(p => p.AiActorExecution)
                .HasForeignKey<AiActorExecution>(e => e.AiActorPlanId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // AiActorConversation configuration
        modelBuilder.Entity<AiActorConversation>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.MessageContent).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(100);

            // Indexes
            // Note: Don't add explicit HasIndex for FK columns - EF Core creates indexes automatically for FKs
            // AiActorId index is created by parent relationship (AiActor.Conversations)
            entity.HasIndex(e => e.TurnNumber);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.AiActorId, e.TurnNumber });

            // Relationships
            // NoAction prevents SQL Server cascade cycle (AiActor -> AiActorConversation and AiActor -> AiActorExecution)
            entity.HasOne(e => e.AiActorExecution)
                .WithMany()
                .HasForeignKey(e => e.AiActorExecutionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // AiActorPlan configuration
        modelBuilder.Entity<AiActorPlan>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Analysis).IsRequired();
            entity.Property(e => e.ActionsJson).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.ReviewedByUserId).HasMaxLength(100);
            entity.Property(e => e.ReviewerComment).HasMaxLength(4000);

            // Indexes
            // Note: Don't add explicit HasIndex for FK columns - EF Core creates indexes automatically for FKs
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ProposedAt);
            entity.HasIndex(e => new { e.AiActorId, e.Status });
            entity.HasIndex(e => new { e.AiActorId, e.ProposedAt });

            // Relationships
            entity.HasOne(e => e.AiActor)
                .WithMany(a => a.Plans)
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction prevents SQL Server cascade cycle error (AiActor -> AiActorPlan -> AiActorPlan creates multiple paths)
            // Application code should explicitly handle ParentPlanId when deleting plans if needed
            entity.HasOne(e => e.ParentPlan)
                .WithMany(p => p.Revisions)
                .HasForeignKey(e => e.ParentPlanId)
                .OnDelete(DeleteBehavior.NoAction);

            // NoAction prevents SQL Server cascade cycle (AiActor -> AiActorPlan -> QueryStepChangeHistory)
            entity.HasMany(e => e.ChangeHistory)
                .WithOne(c => c.AiActorPlan)
                .HasForeignKey(c => c.AiActorPlanId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // QueryStepChangeHistory configuration
        modelBuilder.Entity<QueryStepChangeHistory>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.PreviousSql).IsRequired();
            entity.Property(e => e.NewSql).IsRequired();
            entity.Property(e => e.UserId).HasMaxLength(100);
            entity.Property(e => e.ChangeReason).HasMaxLength(2000);

            // DataSourceId records the step's source at the time of the change; it is no foreign key, so the history
            // outlives the source.

            // Indexes
            // Note: Don't add explicit HasIndex for FK columns - EF Core creates indexes automatically for FKs
            entity.HasIndex(e => e.ChangedAt);
            entity.HasIndex(e => e.ChangeSource);
            entity.HasIndex(e => new { e.QueryStepId, e.ChangedAt });

            // Relationships
            entity.HasOne(e => e.QueryStep)
                .WithMany()
                .HasForeignKey(e => e.QueryStepId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction prevents SQL Server cascade cycles (multiple paths from AiActor hierarchy)
            entity.HasOne(e => e.AiActor)
                .WithMany()
                .HasForeignKey(e => e.AiActorId)
                .OnDelete(DeleteBehavior.NoAction);

            // NoAction prevents SQL Server cascade cycles (AiActor -> AiActorExecution and other paths)
            entity.HasOne(e => e.AiActorExecution)
                .WithMany()
                .HasForeignKey(e => e.AiActorExecutionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        // Add indexes to Query for AiActorId and lock fields
        modelBuilder.Entity<Query>(entity =>
        {
            entity.HasIndex(e => e.AiActorId);
            entity.HasIndex(e => e.IsLocked);
            entity.Property(e => e.LockedByUserId).HasMaxLength(100);

            // The creator (Users.ExternalId, like Subscription.OwnerUserId).
            entity.Property(e => e.OwnerUserId).HasMaxLength(200);
        });

        // Add indexes to Subscription for AiActorId
        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.HasIndex(e => e.AiActorId);

            // The owner scheduled runs act as (Users.ExternalId, like DataContract.OwnerUserId).
            entity.Property(e => e.OwnerUserId).HasMaxLength(200);
            entity.HasIndex(e => e.OwnerUserId).HasDatabaseName("ix_subscriptions_owner_user_id");
        });
    }
}
