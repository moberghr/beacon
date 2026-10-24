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
    protected static void ConfigureTaskEntity(ModelBuilder modelBuilder)
    {
        // QueryTask entity configuration
        modelBuilder.Entity<QueryTask>(entity =>
        {
            // Index for subscription lookup
            entity.HasIndex(t => t.SubscriptionId);

            // Composite index for filtering by resolution status
            entity.HasIndex(t => new { t.Resolved, t.CreatedTime });

            // Global ordering index
            entity.HasIndex(t => t.CreatedTime);

            // Field constraints
            entity.Property(t => t.ResolutionNotes)
                  .HasMaxLength(2000)
                  .IsUnicode(true);

            entity.Property(t => t.AssigneeUserId)
                  .HasMaxLength(100);

            entity.Property(t => t.Priority)
                  .HasConversion<int>()
                  .HasDefaultValue(TaskPriority.Normal);

            entity.HasIndex(t => t.AssigneeUserId);
            entity.HasIndex(t => t.SnoozedUntil);
            entity.HasIndex(t => t.Priority);

            // One-to-many: Task has many Notifications
            entity.HasMany(t => t.Notifications)
                  .WithOne(n => n.Task)
                  .HasForeignKey(n => n.TaskId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.Subscription)
                  .WithMany()
                  .HasForeignKey(t => t.SubscriptionId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
