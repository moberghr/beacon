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
    protected static void ConfigureQueryFolderEntities(ModelBuilder modelBuilder)
    {
        // QueryFolder configuration
        modelBuilder.Entity<QueryFolder>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Path).HasMaxLength(1000).IsRequired();

            // Self-referencing relationship for hierarchy
            entity.HasOne(e => e.ParentFolder)
                .WithMany(e => e.ChildFolders)
                .HasForeignKey(e => e.ParentFolderId)
                .OnDelete(DeleteBehavior.Restrict);

            // Relationship with queries
            entity.HasMany(e => e.Queries)
                .WithOne(q => q.Folder)
                .HasForeignKey(q => q.FolderId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes for performance
            entity.HasIndex(e => e.ParentFolderId);
            entity.HasIndex(e => e.Path);
            entity.HasIndex(e => new { e.ParentFolderId, e.Name }).IsUnique();
            entity.HasIndex(e => new { e.ParentFolderId, e.SortOrder });
            entity.HasIndex(e => e.ArchivedTime);
        });

        // Add index to Query for FolderId
        modelBuilder.Entity<Query>(entity =>
        {
            entity.HasIndex(e => e.FolderId);
        });
    }
}
