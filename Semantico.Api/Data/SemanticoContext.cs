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

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<SubscriptionParameter> SubscriptionParameters => Set<SubscriptionParameter>();

    public DbSet<Query> Queries => Set<Query>();

    public DbSet<QueryParameter> QueryParameters => Set<QueryParameter>();

    public DbSet<Project> Projects => Set<Project>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("semantico");
        modelBuilder.Seed();
        base.OnModelCreating(modelBuilder);
    }
}
