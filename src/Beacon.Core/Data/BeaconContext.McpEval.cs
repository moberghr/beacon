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
    protected static void ConfigureMcpEvalEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpEvalCase>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Question).HasMaxLength(4000).IsRequired();
            entity.Property(e => e.GoldSql).IsRequired();
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.IsActive).HasDefaultValue(true);

            entity.HasIndex(e => new { e.DataSourceId, e.IsActive });
        });

        modelBuilder.Entity<McpEvalRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.TotalCases).HasDefaultValue(0);
            entity.Property(e => e.PassedCases).HasDefaultValue(0);
            entity.Property(e => e.ExecutionAccuracy).HasDefaultValue(0.0);
            entity.Property(e => e.JudgeEnabled).HasDefaultValue(false);

            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.CreatedTime);
        });

        modelBuilder.Entity<McpEvalResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExecutionError).HasMaxLength(4000);
            entity.Property(e => e.JudgeVerdict).HasMaxLength(4000);
            entity.Property(e => e.Passed).HasDefaultValue(false);
            entity.Property(e => e.JudgeUsed).HasDefaultValue(false);
            entity.Property(e => e.ExecutionTimeMs).HasDefaultValue(0);

            entity.HasIndex(e => e.EvalRunId);
            entity.HasIndex(e => e.EvalCaseId);
        });
    }
}
