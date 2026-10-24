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
    protected static void ConfigureProjectEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(2000);

            entity.HasMany(e => e.DataSources)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Repositories)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Documentations)
                .WithOne(e => e.Project)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.Name);

            // The project a host's ExposeDbContext / ExposeDocs / endpoint tools attach to is found by this key, never
            // by its (non-unique, user-editable) name. Ordinary projects leave it null.
            entity.Property(e => e.HostManagedKey).HasMaxLength(200);
            entity.HasIndex(e => e.HostManagedKey).IsUnique();

            // Restricted projects are reached through membership only; no store default, every new row sets it.
            entity.Property(e => e.RequiresMembership).IsRequired();
        });

        modelBuilder.Entity<DataSource>(entity =>
        {
            // Host-managed data sources (ExposeDbContext) are keyed by a stable host key; ordinary ones leave it null.
            entity.Property(e => e.HostManagedKey).HasMaxLength(200);
            entity.Property(e => e.HostModelHash).HasMaxLength(64);
            entity.HasIndex(e => e.HostManagedKey).IsUnique();

            // Every new row sets it (false); the migration that added it opened the sources that existed then.
            entity.Property(e => e.OpenWhenUnlinked).IsRequired();
        });

        modelBuilder.Entity<ProjectDataSource>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.DataSource)
                .WithMany()
                .HasForeignKey(e => e.DataSourceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.ProjectId, e.DataSourceId }).IsUnique();

            // LinkedByUserId names a Users.Id without a foreign key, like ProjectMember.AddedByUserId.
        });

        // A user's role in a project. The names are explicit so both providers create the same objects. No check
        // constraint on Role: handlers validate it. AddedByUserId is no foreign key (a second path from Users).
        modelBuilder.Entity<ProjectMember>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pk_project_members");
            entity.Property(e => e.Role).HasConversion<int>();

            entity.HasOne(e => e.Project)
                .WithMany(e => e.Members)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_project_members_projects_project_id");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_project_members_users_user_id");

            entity.HasIndex(e => new { e.ProjectId, e.UserId })
                .IsUnique()
                .HasDatabaseName("ux_project_members_project_id_user_id");
            entity.HasIndex(e => e.UserId).HasDatabaseName("ix_project_members_user_id");
        });

        modelBuilder.Entity<GitHubRepository>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RepositoryUrl).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Branch).HasMaxLength(200);
            entity.Property(e => e.ScanCronExpression).HasMaxLength(100);
            entity.Property(e => e.LastScanError).HasMaxLength(4000);

            entity.HasMany(e => e.CodeReferences)
                .WithOne(e => e.GitHubRepository)
                .HasForeignKey(e => e.GitHubRepositoryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.ScanStatus);
        });

        modelBuilder.Entity<CodeReference>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.SchemaName).HasMaxLength(200);
            entity.Property(e => e.TableName).HasMaxLength(200);
            entity.Property(e => e.ColumnName).HasMaxLength(200);
            entity.Property(e => e.CodeSnippet).HasMaxLength(4000);
            entity.Property(e => e.ClassName).HasMaxLength(200);
            entity.Property(e => e.MethodName).HasMaxLength(200);

            entity.HasIndex(e => e.GitHubRepositoryId);
            entity.HasIndex(e => new { e.SchemaName, e.TableName });
            entity.HasIndex(e => e.ReferenceType);
        });

        modelBuilder.Entity<ProjectDocumentation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.GeneratedAt);
        });

        modelBuilder.Entity<ProjectDocumentationSection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SectionType);

            entity.HasOne(e => e.Documentation)
                .WithMany(d => d.Sections)
                .HasForeignKey(e => e.ProjectDocumentationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectImportedDocument>(entity =>
        {
            // Host-shipped documents (ExposeDocs). Identity is (ProjectId, SourceKey, Path); the unique index covers
            // archived rows too, so the sync loads them with IgnoreQueryFilters and un-archives instead of inserting.
            entity.HasKey(e => e.Id);
            // Key columns sized so the unique index stays under SQL Server's 1700-byte nonclustered key limit.
            entity.Property(e => e.SourceKey).HasMaxLength(ProjectImportedDocument.MaxSourceKeyLength).IsRequired();
            entity.Property(e => e.Path).HasMaxLength(ProjectImportedDocument.MaxPathLength).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Content).IsRequired();

            entity.HasIndex(e => new { e.ProjectId, e.SourceKey, e.Path }).IsUnique();

            entity.HasOne(e => e.Project)
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

    }
}
