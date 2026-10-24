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
    protected static void ConfigureAnomalyEntities(ModelBuilder modelBuilder)
    {
        // AnomalyConfig configuration
        modelBuilder.Entity<AnomalyConfig>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Relationships
            entity.HasOne(e => e.Subscription)
                  .WithMany()
                  .HasForeignKey(e => e.SubscriptionId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Unique index: one config per subscription
            entity.HasIndex(e => e.SubscriptionId)
                  .IsUnique();

            // Index for enabled configs
            entity.HasIndex(e => e.Enabled);
        });

        // AnomalyBaseline configuration
        modelBuilder.Entity<AnomalyBaseline>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Relationships
            entity.HasOne(e => e.Subscription)
                  .WithMany()
                  .HasForeignKey(e => e.SubscriptionId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Index for efficient time-based queries
            entity.HasIndex(e => new { e.SubscriptionId, e.ExecutionTime });

            // Index for lookback queries
            entity.HasIndex(e => e.ExecutionTime);
        });

        // AnomalyEvent configuration
        modelBuilder.Entity<AnomalyEvent>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Severity)
                  .HasMaxLength(20)
                  .IsRequired();

            entity.Property(e => e.Explanation)
                  .HasMaxLength(2000);

            entity.Property(e => e.AcknowledgedBy)
                  .HasMaxLength(200);

            // Relationships
            // NoAction prevents SQL Server cascade cycle error (Subscription -> Notification -> AnomalyEvent creates multiple paths)
            // Application code should explicitly delete AnomalyEvents when deleting Subscriptions if needed
            entity.HasOne(e => e.Subscription)
                  .WithMany()
                  .HasForeignKey(e => e.SubscriptionId)
                  .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.Notification)
                  .WithMany()
                  .HasForeignKey(e => e.NotificationId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Indexes for querying
            entity.HasIndex(e => new { e.SubscriptionId, e.DetectedTime });
            entity.HasIndex(e => new { e.Acknowledged, e.DetectedTime });
            entity.HasIndex(e => e.DetectedTime);
            entity.HasIndex(e => e.NotificationId);
        });
    }
}
