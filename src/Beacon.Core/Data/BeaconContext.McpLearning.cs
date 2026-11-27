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
    protected static void ConfigureMcpLearningEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpQuerySignal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Tool).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Question).HasMaxLength(4000).IsRequired();
            entity.Property(e => e.IntentClassification).HasMaxLength(50);
            entity.Property(e => e.SchemaValidationError).HasMaxLength(4000);
            entity.Property(e => e.ExecutionError).HasMaxLength(4000);
            entity.Property(e => e.DryRunError).HasMaxLength(4000);
            entity.Property(e => e.CallerHash).HasMaxLength(128);
            entity.Property(e => e.CandidateSqlHash).HasMaxLength(64);

            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => e.CreatedTime);
            entity.HasIndex(e => e.IsSuccessful);
            // No foreign key: SQL Server refuses a self-referencing set-null, and a parent removed by retention leaves a
            // harmless dangling id.
            entity.HasIndex(e => e.ParentSignalId);
        });

        // The knowledge items an ask signal's prompt carried, deleted with their signal (so the retention job's bulk delete
        // of signals removes them). Ids and enums only.
        modelBuilder.Entity<McpSignalContextItem>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.Signal)
                .WithMany(e => e.ContextItems)
                .HasForeignKey(e => e.SignalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.SignalId);
            entity.HasIndex(e => new { e.Kind, e.ItemId });
        });

        // How the ask signals that carried a knowledge item went, one row per project and item, recomputed by the learning
        // job. No foreign key: the item id points at a different table by kind. Ids, counts and scores only.
        modelBuilder.Entity<McpKnowledgeItemStat>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.ProjectId, e.Kind, e.ItemId }).IsUnique();
        });

        modelBuilder.Entity<McpLearnedPattern>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SchemaName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TableName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ColumnName).HasMaxLength(200);
            entity.Property(e => e.PatternContent).IsRequired();
            entity.Property(e => e.ExampleQuestion).HasMaxLength(4000);
            entity.Property(e => e.ContentHash).HasMaxLength(64);

            entity.HasIndex(e => new { e.DataSourceId, e.Status, e.TableName });
            entity.HasIndex(e => e.ProjectId);
        });

        modelBuilder.Entity<McpDocumentationPatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TargetIdentifier).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ProposedContent).IsRequired();
            entity.Property(e => e.Reasoning).HasMaxLength(2000).IsRequired();

            entity.HasIndex(e => new { e.ProjectId, e.Status });
            entity.HasIndex(e => e.DataSourceId);
        });
    }
}
