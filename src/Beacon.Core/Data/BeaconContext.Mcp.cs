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
    protected static void ConfigureMcpEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpAuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Tool).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Parameters).HasMaxLength(4000);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);
            entity.Property(e => e.CallerKind).HasMaxLength(16);
            entity.Property(e => e.CallerHash).HasMaxLength(64);
            entity.Property(e => e.CorrelationId).HasMaxLength(32);
            entity.Property(e => e.TraceId).HasMaxLength(32);
            entity.Property(e => e.SpanId).HasMaxLength(16);
            entity.Property(e => e.McpSessionId).HasMaxLength(128);
            entity.Property(e => e.UpstreamRequestId).HasMaxLength(128);
            entity.Property(e => e.ContentHash).HasMaxLength(64);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedTime);
            entity.HasIndex(e => e.Tool);
        });

        modelBuilder.Entity<MaintenanceHeartbeat>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(64).IsRequired();

            entity.HasIndex(e => e.Name).IsUnique();
        });

        // One row per statement an audited call ran on a data source, deleted with its audit row (so the audit purge
        // covers it). No key to the data source or the project: the audit outlives both.
        modelBuilder.Entity<McpAuditStatement>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Purpose).HasMaxLength(32).IsRequired();
            entity.Property(e => e.SqlHash).HasMaxLength(64);
            entity.Property(e => e.TablesJson).HasMaxLength(4000);
            entity.Property(e => e.ErrorClass).HasMaxLength(64);

            entity.HasOne(e => e.AuditLog)
                .WithMany(e => e.Statements)
                .HasForeignKey(e => e.McpAuditLogId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.McpAuditLogId);
        });

        modelBuilder.Entity<McpSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AskSystemPrompt).HasMaxLength(4000);
            entity.Property(e => e.GlobalInstruction).HasMaxLength(4000);
            entity.Property(e => e.GetContextDescription).HasMaxLength(1000);
            entity.Property(e => e.SearchDescription).HasMaxLength(1000);
            entity.Property(e => e.QueryDescription).HasMaxLength(1000);
            entity.Property(e => e.GetDocumentationDescription).HasMaxLength(1000);
            entity.Property(e => e.AskDescription).HasMaxLength(1000);
            entity.Property(e => e.CustomPiiPatterns).HasMaxLength(4000);
            entity.Property(e => e.EnableSampleValueCollection).HasDefaultValue(false);
            entity.Property(e => e.EnableReplayVerification).HasDefaultValue(true);
            entity.Property(e => e.LearningReplayMinFlips).HasDefaultValue(1);
            entity.Property(e => e.EnableContextualRetrieval).HasDefaultValue(false);
            entity.Property(e => e.DocChunkWindowSentences).HasDefaultValue(5);
            entity.Property(e => e.DocChunkOverlapSentences).HasDefaultValue(1);
            entity.Property(e => e.GlossaryTopK).HasDefaultValue(5);
            entity.Property(e => e.DocChunkTopK).HasDefaultValue(6);
            entity.Property(e => e.RetainQueryContent).HasDefaultValue(true);
            entity.Property(e => e.StatementTimeoutSeconds).HasDefaultValue(30);
            entity.Property(e => e.MaxResultBytes).HasDefaultValue(262144);
            entity.Property(e => e.MaxExplainCost).HasPrecision(18, 2);
            entity.Property(e => e.MaxConcurrentQueriesPerKey).HasDefaultValue(4);
            entity.Property(e => e.AllowExplicitFeedbackContent).HasDefaultValue(true);
        });

        // Per-project overrides: one row per project, every column nullable (null = inherit the global row).
        // The row is deleted with its project; resolution lives in McpSettingsProvider.
        modelBuilder.Entity<McpProjectSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId).IsUnique();
            entity.HasOne(e => e.Project)
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.CustomPiiPatterns).HasMaxLength(4000);
            entity.Property(e => e.MaxExplainCost).HasPrecision(18, 2);
        });
    }
}
