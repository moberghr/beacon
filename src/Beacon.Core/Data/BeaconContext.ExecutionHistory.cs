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
    protected static void ConfigureExecutionHistoryEntity(ModelBuilder modelBuilder)
    {
        // QueryExecutionHistory is the highest-volume table (one row per execution).
        // It previously relied on conventions only (PK + auto FK index on SubscriptionId),
        // which left the time-window + status filters used by the Home dashboards,
        // uptime rail, and trend charts to full table scans as the table grows.
        modelBuilder.Entity<QueryExecutionHistory>(entity =>
        {
            // Time-window scans (activity feed, uptime rail, trends all filter CreatedTime).
            entity.HasIndex(e => e.CreatedTime);

            // Status + time filters (e.g. recent failed/timeout executions on the activity feed).
            entity.HasIndex(e => new { e.NotificationStatus, e.CreatedTime });

            // Per-subscription history ordered by time (subscription detail, anomaly lookbacks).
            entity.HasIndex(e => new { e.SubscriptionId, e.CreatedTime });
        });
    }
}
