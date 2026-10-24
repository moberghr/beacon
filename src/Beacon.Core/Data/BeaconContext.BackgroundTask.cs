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
    // One task of a kind per subject is queued or running at a time: a second start fails on this index. Filter column names
    // differ per provider (SQL Server keeps PascalCase, PostgreSQL uses snake_case via the naming convention).
    protected void ConfigureBackgroundTaskEntities(ModelBuilder modelBuilder)
    {
        var unfinishedFilter = Database.ProviderName == HostModelReader.SqlServerProviderName
            ? $"[State] IN ({(int)BackgroundTaskState.Queued}, {(int)BackgroundTaskState.Running})"
            : $"state IN ({(int)BackgroundTaskState.Queued}, {(int)BackgroundTaskState.Running})";
        modelBuilder.Entity<BackgroundTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.JobId).HasMaxLength(64);
            entity.Property(e => e.FailureCode).HasMaxLength(64);
            entity.Property(e => e.PermissionSource).HasMaxLength(16);
            entity.Property(e => e.RequesterStampHash).HasMaxLength(64);

            entity.HasIndex(e => new { e.Kind, e.SubjectId })
                .IsUnique()
                .HasFilter(unfinishedFilter);
        });
    }
}
