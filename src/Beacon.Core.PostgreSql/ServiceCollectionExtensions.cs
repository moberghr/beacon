using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Beacon.Core.Data;
using Beacon.Core.PostgreSql.Data;

namespace Beacon.Core.PostgreSql;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures PostgreSQL as the database provider for Beacon.
    /// This method is chainable after AddBeaconServices().
    /// </summary>
    public static IServiceCollection UsePostgreSql(
        this BeaconBuilder builder,
        string connectionString,
        string schema = "beacon")
    {
        var dataSource = new NpgsqlDataSourceBuilder(connectionString)
        {
            ConnectionStringBuilder = { SearchPath = schema }
        }.Build();

        // TODO(B5): vector type handler / raw-SQL vector query.
        // The pgvector type mapping (data-source-level UseVector() on NpgsqlDataSourceBuilder,
        // plus o.UseVector() in UseNpgsql) is intentionally NOT wired here: the McpEmbedding EF
        // model has no Pgvector.Vector property (the vector(384) column is DB-managed only), and
        // enabling it cleanly requires restructuring how the data source is built. B5's PG vector
        // query can instead cast with a ::vector string literal and the <=> operator.
        builder.Services.AddDbContextFactory<PostgreSqlBeaconContext>(options =>
            options.UseNpgsql(dataSource)
                   .UseSnakeCaseNamingConvention());

        // Register the base context factory using the PostgreSQL implementation
        builder.Services.AddSingleton<IDbContextFactory<BeaconContext>>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<PostgreSqlBeaconContext>>();
            return new BeaconContextFactoryAdapter(factory);
        });

        // Register BeaconContext as scoped (required for Data Protection key persistence)
        builder.Services.AddScoped<BeaconContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext());

        return builder.Services;
    }

    private class BeaconContextFactoryAdapter : IDbContextFactory<BeaconContext>
    {
        private readonly IDbContextFactory<PostgreSqlBeaconContext> _factory;

        public BeaconContextFactoryAdapter(IDbContextFactory<PostgreSqlBeaconContext> factory)
        {
            _factory = factory;
        }

        public BeaconContext CreateDbContext()
        {
            return _factory.CreateDbContext();
        }
    }
}
