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
    protected static void ConfigureTaskWatcherEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskWatcher>(entity =>
        {
            // Composite key
            entity.HasKey(w => new { w.QueryTaskId, w.UserId });

            entity.Property(w => w.UserId)
                  .HasMaxLength(100)
                  .IsRequired();

            entity.HasOne(w => w.QueryTask)
                  .WithMany(t => t.Watchers)
                  .HasForeignKey(w => w.QueryTaskId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(w => w.UserId);
        });
    }
}
