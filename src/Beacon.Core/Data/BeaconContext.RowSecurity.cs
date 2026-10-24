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
    protected void ConfigureRowSecurityEntities(ModelBuilder modelBuilder)
    {
        // Table names and attribute values are kept exactly as entered: PostgreSQL compares them ordinally, and SQL Server
        // does with a binary collation (its default one ignores case), so a case variant is another table or value on both.
        var ordinalCollation = Database.ProviderName == HostModelReader.SqlServerProviderName
            ? "Latin1_General_100_BIN2"
            : null;

        modelBuilder.Entity<AttributeDefinition>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Key).HasMaxLength(63).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);

            // Policies name an attribute by its key: unique across live attributes. Filter column names differ per
            // provider (SQL Server keeps PascalCase, PostgreSQL uses snake_case via the naming convention).
            var archivedTimeFilter = Database.ProviderName == HostModelReader.SqlServerProviderName
                ? "[ArchivedTime] IS NULL"
                : "archived_time IS NULL";
            entity.HasIndex(e => e.Key)
                .IsUnique()
                .HasFilter(archivedTimeFilter);

            entity.HasMany(e => e.Values)
                .WithOne(e => e.AttributeDefinition)
                .HasForeignKey(e => e.AttributeDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserAttributeValue>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Value).HasMaxLength(256).IsRequired().UseCollation(ordinalCollation);
            entity.Property(e => e.CreatedByUserId).HasMaxLength(200);

            // A user holds a value once; the index also serves reading a user's values.
            entity.HasIndex(e => new { e.UserId, e.AttributeDefinitionId, e.Value }).IsUnique();
            entity.HasIndex(e => e.AttributeDefinitionId);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RowPolicy>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.SchemaName).HasMaxLength(200).IsRequired().UseCollation(ordinalCollation);
            entity.Property(e => e.TableName).HasMaxLength(200).IsRequired().UseCollation(ordinalCollation);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);

            entity.HasIndex(e => new { e.DataSourceId, e.SchemaName, e.TableName });

            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.Versions)
                .WithOne(e => e.RowPolicy)
                .HasForeignKey(e => e.RowPolicyId)
                .OnDelete(DeleteBehavior.Cascade);

            // NoAction: SQL Server refuses a second cascade path between the policies and their versions.
            entity.HasOne(e => e.ActiveVersion)
                .WithMany()
                .HasForeignKey(e => e.ActiveVersionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<RowPolicyVersion>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.DefinitionJson).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CreatedByUserId).HasMaxLength(200);

            entity.HasIndex(e => new { e.RowPolicyId, e.VersionNumber }).IsUnique();
            entity.HasIndex(e => new { e.RowPolicyId, e.Status });
        });

        modelBuilder.Entity<TableClassification>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.SchemaName).HasMaxLength(200).IsRequired().UseCollation(ordinalCollation);
            entity.Property(e => e.TableName).HasMaxLength(200).IsRequired().UseCollation(ordinalCollation);

            // One class per table of a source.
            entity.HasIndex(e => new { e.DataSourceId, e.SchemaName, e.TableName }).IsUnique();

            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
