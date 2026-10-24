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
    protected void ConfigureUserManagementEntities(ModelBuilder modelBuilder)
    {
        // BeaconUser configuration
        modelBuilder.Entity<BeaconUser>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ExternalId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.IdentityProvider).HasMaxLength(500);
            entity.Property(e => e.UserName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(255);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.PasswordHash).HasMaxLength(500);
            entity.Property(e => e.PasswordSalt).HasMaxLength(500);
            entity.Property(e => e.SecurityStamp).HasMaxLength(64);

            // Unique constraint on ExternalId
            entity.HasIndex(e => e.ExternalId).IsUnique();
            entity.HasIndex(e => new { e.IdentityProvider, e.ExternalId });

            // Unique constraint on UserName (only for non-archived users).
            // Filter column name differs per provider: SQL Server keeps PascalCase,
            // PostgreSQL uses snake_case via the naming convention.
            var archivedTimeFilter = Database.ProviderName == HostModelReader.SqlServerProviderName
                ? "[ArchivedTime] IS NULL"
                : "archived_time IS NULL";
            entity.HasIndex(e => e.UserName)
                .IsUnique()
                .HasFilter(archivedTimeFilter);

            // Indexes for querying
            entity.HasIndex(e => e.Email);
            entity.HasIndex(e => e.IsEnabled);
            entity.HasIndex(e => e.IsSuperAdmin);
            entity.HasIndex(e => e.IsInternalUser);
            entity.HasIndex(e => e.ArchivedTime);

            // Relationships
            entity.HasMany(e => e.UserRoles)
                .WithOne(ur => ur.User)
                .HasForeignKey(ur => ur.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BeaconRole configuration
        modelBuilder.Entity<BeaconRole>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);

            // Unique constraint on role name
            entity.HasIndex(e => e.Name).IsUnique();

            // Relationships
            entity.HasMany(e => e.UserRoles)
                .WithOne(ur => ur.Role)
                .HasForeignKey(ur => ur.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BeaconUserRole configuration
        modelBuilder.Entity<BeaconUserRole>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.AssignedByUserId).HasMaxLength(200);

            // Unique constraint on user-role combination
            entity.HasIndex(e => new { e.UserId, e.RoleId }).IsUnique();

            // Indexes for querying
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.RoleId);
            entity.HasIndex(e => e.AssignedAt);
        });
    }
}
