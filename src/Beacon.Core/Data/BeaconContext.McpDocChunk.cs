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
    protected static void ConfigureMcpDocChunkEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpDocChunk>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ChunkText).IsRequired();

            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => new { e.ProjectId, e.SourceSectionId });

            // Chunks of an imported document go with it; the sync deletes them explicitly on change/archive.
            entity.HasOne(e => e.ImportedDocument)
                .WithMany()
                .HasForeignKey(e => e.ImportedDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
