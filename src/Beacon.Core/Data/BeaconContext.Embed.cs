using Beacon.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Data;

public abstract partial class BeaconContext
{
    // Embed grants (revocable permissions to embed one approved saved-query version)
    public DbSet<EmbedGrant> EmbedGrants => Set<EmbedGrant>();

    protected static void ConfigureEmbedEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmbedGrant>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.PublicId).HasMaxLength(32).IsFixedLength().IsUnicode(false).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.AllowedOriginsJson).IsRequired();
            entity.Property(e => e.FixedParametersJson).IsRequired();
            entity.Property(e => e.LockedParameterNamesJson).IsRequired();
            entity.Property(e => e.SecretHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
            entity.Property(e => e.RevokedByUserId).HasMaxLength(200);

            // Tokens and URLs name a grant by its public id; an exchange finds it by its secret's hash.
            entity.HasIndex(e => e.PublicId).IsUnique();
            entity.HasIndex(e => e.SecretHash).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.RevokedTime });

            // A grant is never deleted, and neither is what it names: projects and queries are archived.
            entity.HasOne(e => e.Project)
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Query)
                .WithMany()
                .HasForeignKey(e => e.QueryId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.QueryVersion)
                .WithMany()
                .HasForeignKey(e => e.QueryVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
