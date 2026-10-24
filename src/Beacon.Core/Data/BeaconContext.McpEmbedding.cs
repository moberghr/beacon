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
    protected static void ConfigureMcpEmbeddingEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpEmbedding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmbeddingBytes).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(200).IsRequired();

            entity.HasIndex(e => e.DataSourceId);
            entity.HasIndex(e => new { e.DataSourceId, e.OwnerType, e.OwnerId }).IsUnique();

            // Project-scoped owner lookup for doc-chunk / glossary retrieval (WHERE project_id = X AND owner_type = ANY(...)).
            entity.HasIndex(e => new { e.ProjectId, e.OwnerType });
        });
    }
}
