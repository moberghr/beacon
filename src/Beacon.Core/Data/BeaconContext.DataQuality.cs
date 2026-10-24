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
    protected static void ConfigureDataQualityEntities(ModelBuilder modelBuilder)
    {
        // DataContract configuration
        modelBuilder.Entity<DataContract>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.SchemaName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TableName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.CronExpression).HasMaxLength(100).IsRequired();
            entity.Property(e => e.OwnerUserId).HasMaxLength(100);
            entity.Property(e => e.OwnerNotAuthorizedSince);

            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.Rules)
                .WithOne(r => r.DataContract)
                .HasForeignKey(r => r.DataContractId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Evaluations)
                .WithOne(e => e.DataContract)
                .HasForeignKey(e => e.DataContractId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => e.IsEnabled);
            entity.HasIndex(e => e.ArchivedTime);
            entity.HasIndex(e => new { e.DataSourceId, e.SchemaName, e.TableName });
        });

        // DataContractRule configuration
        modelBuilder.Entity<DataContractRule>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.ColumnName).HasMaxLength(200);
            entity.Property(e => e.Configuration).IsRequired();

            entity.HasIndex(e => e.DataContractId);
        });

        // DataQualityEvaluation configuration
        modelBuilder.Entity<DataQualityEvaluation>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasMany(e => e.RuleResults)
                .WithOne(r => r.DataQualityEvaluation)
                .HasForeignKey(r => r.DataQualityEvaluationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.DataContractId);
            entity.HasIndex(e => e.CreatedTime);
        });

        // DataQualityRuleResult configuration
        modelBuilder.Entity<DataQualityRuleResult>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ActualValue).HasMaxLength(500);
            entity.Property(e => e.ExpectedValue).HasMaxLength(500);
            entity.Property(e => e.Message).HasMaxLength(2000);

            // NoAction to avoid multi-path cascade (DataContract -> Rule -> RuleResult and DataContract -> Evaluation -> RuleResult)
            entity.HasOne(e => e.DataContractRule)
                .WithMany()
                .HasForeignKey(e => e.DataContractRuleId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasIndex(e => e.DataQualityEvaluationId);
            entity.HasIndex(e => e.DataContractRuleId);
        });

        // DataQualityScore configuration
        modelBuilder.Entity<DataQualityScore>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.SchemaName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TableName).HasMaxLength(200).IsRequired();

            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.DataSourceId, e.SchemaName, e.TableName }).IsUnique();
            entity.HasIndex(e => e.EvaluatedAt);
        });
    }
}
