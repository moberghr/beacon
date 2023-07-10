using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data.Entities;

namespace Semantico.Api.Data;

public class SemanticoContext : DbContext
{
    public SemanticoContext(DbContextOptions<SemanticoContext> options)
       : base(options)
    {
    }

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<Query> Queries => Set<Query>();

    public DbSet<Project> Projects => Set<Project>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("semantico");
        base.OnModelCreating(modelBuilder);
    }
}
