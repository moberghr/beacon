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
    // The security audit trail: append-only rows (the interceptor refuses updates and deletes), no foreign keys (an event
    // outlives the user, key or project it names). Read by time, by actor and by resource.
    protected static void ConfigureSecurityAuditEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SecurityAuditEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Category).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Outcome).HasMaxLength(16).IsRequired();
            entity.Property(e => e.ActorKind).HasMaxLength(16).IsRequired();
            entity.Property(e => e.CallerHash).HasMaxLength(64);
            entity.Property(e => e.ResourceType).HasMaxLength(32);
            entity.Property(e => e.ResourceId).HasMaxLength(64);
            entity.Property(e => e.DetailsJson).HasMaxLength(4000);
            entity.Property(e => e.TraceId).HasMaxLength(32);
            entity.Property(e => e.KeyId).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Hmac).HasMaxLength(64).IsRequired();

            entity.HasIndex(e => e.EventGuid).IsUnique();
            entity.HasIndex(e => e.CreatedTime);
            entity.HasIndex(e => new { e.ActorUserId, e.CreatedTime });
            entity.HasIndex(e => new { e.ResourceType, e.ResourceId });
        });
    }
}
