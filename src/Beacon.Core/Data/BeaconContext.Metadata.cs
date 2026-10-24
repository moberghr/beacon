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
    protected static void ConfigureMetadataEntities(ModelBuilder modelBuilder)
    {
        // DatabaseMetadata configuration
        modelBuilder.Entity<DatabaseMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SchemaName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.TableName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TableDescription).HasMaxLength(1000);

            // Relationships
            entity.HasOne(e => e.DataSource)
                  .WithMany()
                  .HasForeignKey(e => e.DataSourceId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Columns)
                  .WithOne(e => e.DatabaseMetadata)
                  .HasForeignKey(e => e.DatabaseMetadataId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Indexes)
                  .WithOne(e => e.DatabaseMetadata)
                  .HasForeignKey(e => e.DatabaseMetadataId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Indexes for performance
            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => new { e.DataSourceId, e.SchemaName, e.TableName }).IsUnique();
            entity.HasIndex(e => e.LastRefreshed);
        });

        // ColumnMetadata configuration
        modelBuilder.Entity<ColumnMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ColumnName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.DataType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ForeignKeyTable).HasMaxLength(200);
            entity.Property(e => e.ForeignKeyColumn).HasMaxLength(200);
            entity.Property(e => e.ForeignKeySchema).HasMaxLength(100);
            entity.Property(e => e.ForeignKeyConstraintName).HasMaxLength(300);
            entity.Property(e => e.DefaultValue).HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.SampleValues).HasMaxLength(2000);

            // Indexes for performance
            entity.HasIndex(e => e.DatabaseMetadataId);
            entity.HasIndex(e => new { e.DatabaseMetadataId, e.ColumnName });
        });

        // IndexMetadata configuration
        modelBuilder.Entity<IndexMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.IndexName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Columns).IsRequired();

            // Indexes for performance
            entity.HasIndex(e => e.DatabaseMetadataId);
        });

        // SchemaRelationship configuration
        modelBuilder.Entity<SchemaRelationship>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceSchema).HasMaxLength(100).IsRequired();
            entity.Property(e => e.SourceTable).HasMaxLength(200).IsRequired();
            entity.Property(e => e.SourceColumn).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TargetSchema).HasMaxLength(100).IsRequired();
            entity.Property(e => e.TargetTable).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TargetColumn).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Label).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ConstraintName).HasMaxLength(300);

            entity.HasOne(e => e.DataSource)
                  .WithMany()
                  .HasForeignKey(e => e.DataSourceId)
                  .OnDelete(DeleteBehavior.Cascade);

            // One edge per column pair per data source — sync re-runs must not duplicate.
            entity.HasIndex(e => new
            {
                e.DataSourceId,
                e.SourceSchema,
                e.SourceTable,
                e.SourceColumn,
                e.TargetSchema,
                e.TargetTable,
                e.TargetColumn
            }).IsUnique();

            entity.HasIndex(e => new { e.DataSourceId, e.Origin });
        });
    }
}
