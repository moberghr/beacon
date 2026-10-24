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
    protected static void ConfigureAppSettingEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Key).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Value).HasMaxLength(2000);
            entity.Property(e => e.Category).HasMaxLength(50).IsRequired();

            entity.HasIndex(e => e.Key).IsUnique();
            entity.HasIndex(e => e.Category);
        });

        modelBuilder.Entity<AppSettingHistory>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.SettingKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.OldValue).HasMaxLength(2000);
            entity.Property(e => e.NewValue).HasMaxLength(2000);
            entity.Property(e => e.ChangedByUserId).HasMaxLength(200);
            entity.Property(e => e.HmacKeyId).HasMaxLength(32);
            entity.Property(e => e.Hmac).HasMaxLength(64);

            entity.HasIndex(e => e.SettingKey);
            entity.HasIndex(e => e.ChangedAt);
            entity.HasIndex(e => e.AuditGuid).IsUnique();
        });
    }
}
