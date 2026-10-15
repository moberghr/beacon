using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Beacon.Core.Data;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.PostgreSql.Data;

namespace Beacon.Core.PostgreSql;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures PostgreSQL as the database provider for Beacon, reading the connection string from
    /// <c>ConnectionStrings:BeaconContext</c> and the schema from <c>Beacon:Schema</c>
    /// (defaults to <c>beacon</c>).
    /// </summary>
    public static IServiceCollection UsePostgreSql(this BeaconBuilder builder)
    {
        return builder.UsePostgreSql(
            BeaconDatabaseConfiguration.GetConnectionString(builder.Configuration),
            BeaconDatabaseConfiguration.GetSchema(builder.Configuration));
    }

    /// <summary>
    /// Configures PostgreSQL as the database provider for Beacon.
    /// This method is chainable after AddBeaconServices().
    /// </summary>
    public static IServiceCollection UsePostgreSql(
        this BeaconBuilder builder,
        string connectionString,
        string schema = BeaconDatabaseConfiguration.DefaultSchema)
    {
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);

        // The schema comes first so Beacon's own unqualified names resolve there and never fall through to a
        // host table. public follows because a database that already has pgvector usually has it there:
        // AddMcpEmbedding's CREATE EXTENSION IF NOT EXISTS is then a no-op, and its unqualified vector(384) /
        // vector_cosine_ops (plus the runtime ::vector / <=>) resolve only through the search path. On a
        // database without pgvector that migration still installs it into the schema, and a DBA can later move
        // it to public (ALTER EXTENSION vector SET SCHEMA public) without breaking Beacon.
        var dataSource = new NpgsqlDataSourceBuilder(connectionString)
        {
            ConnectionStringBuilder = { SearchPath = $"{schema},public" }
        }.Build();

        return builder.AddPostgreSqlContext(_ => dataSource, schema, setSearchPathOnOpen: false);
    }

    /// <summary>
    /// Configures PostgreSQL as Beacon's database over a host-owned <see cref="NpgsqlDataSource"/> — for example
    /// one that authenticates with Entra tokens and has no password in its connection string. Every Beacon
    /// connection (EF Core, migrations in <c>UseBeacon()</c>, raw SQL) is opened from it. See the factory
    /// overload for what Beacon requires of the data source.
    /// </summary>
    public static IServiceCollection UsePostgreSql(
        this BeaconBuilder builder,
        NpgsqlDataSource dataSource,
        string schema = BeaconDatabaseConfiguration.DefaultSchema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        return builder.UsePostgreSql(_ => dataSource, schema);
    }

    /// <summary>
    /// Configures PostgreSQL as Beacon's database over a host-owned <see cref="NpgsqlDataSource"/> resolved from the
    /// container, e.g. <c>sp =&gt; sp.GetRequiredService&lt;NpgsqlDataSource&gt;()</c> for the singleton Aspire's
    /// <c>AddNpgsqlDataSource</c>/<c>AddAzureNpgsqlDataSource</c> registers. It is resolved once, from the root
    /// provider, the first time Beacon's context options are built.
    /// <para>
    /// The host keeps its own search path: instead of changing the data source, Beacon runs
    /// <c>SET search_path TO beacon, public</c> on each connection it opens, so its unqualified names resolve
    /// to the <c>beacon</c> schema and pgvector in <c>public</c>. Npgsql discards that setting when the connection
    /// returns to the pool, so the data source must keep connection reset on (no <c>No Reset On Close</c>) and
    /// must not use <c>Multiplexing</c>.
    /// </para>
    /// <para>
    /// No pgvector type mapping (<c>UseVector()</c>) is needed on the data source: Beacon never reads or binds the
    /// vector type, it casts text literals to <c>vector(384)</c> in SQL. The database does need the
    /// <c>vector</c> extension, which Beacon's migrations create if missing.
    /// </para>
    /// </summary>
    public static IServiceCollection UsePostgreSql(
        this BeaconBuilder builder,
        Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory,
        string schema = BeaconDatabaseConfiguration.DefaultSchema)
    {
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);

        return builder.AddPostgreSqlContext(dataSourceFactory, schema, setSearchPathOnOpen: true);
    }

    private static IServiceCollection AddPostgreSqlContext(
        this BeaconBuilder builder,
        Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory,
        string schema,
        bool setSearchPathOnOpen)
    {
        // TODO(B5): vector type handler / raw-SQL vector query.
        // The pgvector type mapping (data-source-level UseVector() on NpgsqlDataSourceBuilder,
        // plus o.UseVector() in UseNpgsql) is intentionally NOT wired here: the McpEmbedding EF
        // model has no Pgvector.Vector property (the vector(384) column is DB-managed only), and
        // enabling it cleanly requires restructuring how the data source is built. B5's PG vector
        // query can instead cast with a ::vector string literal and the <=> operator.
        builder.Services.AddDbContextFactory<PostgreSqlBeaconContext>((sp, options) =>
        {
            options.UseNpgsql(dataSourceFactory(sp),
                    o => o.MigrationsHistoryTable("__EFMigrationsHistory", schema))
                   .UseSnakeCaseNamingConvention()
                   .AddInterceptors(sp.GetRequiredService<ContentRetentionInterceptor>(), sp.GetRequiredService<SecurityAuditInterceptor>())
                   .UseBeaconSchema(schema)
                   .ReplaceService<IMigrationsSqlGenerator, SchemaAwareMigrationsSqlGenerator>()
                   .ReplaceService<IModelCacheKeyFactory, BeaconSchemaModelCacheKeyFactory>();

            // A host data source carries the host's search path, not Beacon's (see the factory overload).
            if (setSearchPathOnOpen)
            {
                options.AddInterceptors(new BeaconSearchPathInterceptor(schema));
            }
        });

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
