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
    protected static void ConfigureMcpGlossaryEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpGlossaryTerm>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Term).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Synonyms).HasMaxLength(1000);
            entity.Property(e => e.Definition).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.TargetSchema).HasMaxLength(200);
            entity.Property(e => e.TargetTable).HasMaxLength(200);
            entity.Property(e => e.TargetColumn).HasMaxLength(200);
            entity.Property(e => e.MetricExpression).HasMaxLength(1000);
            entity.Property(e => e.IsActive).HasDefaultValue(true);

            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => new { e.ProjectId, e.IsActive });
        });
    }
}
