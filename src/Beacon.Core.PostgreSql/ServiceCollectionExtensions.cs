using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
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
    private const string Schema = BeaconDatabaseConfiguration.DefaultSchema;

    /// <summary>
    /// Configures PostgreSQL as the database provider for Beacon, reading the connection string from
    /// <c>ConnectionStrings:BeaconContext</c>. Beacon's schema is always <c>beacon</c>; <c>Beacon:Schema</c> set to any
    /// other value is refused.
    /// </summary>
    public static IServiceCollection UsePostgreSql(this BeaconBuilder builder)
    {
        var connectionString = BeaconDatabaseConfiguration.GetConnectionString(builder.Configuration);
        BeaconDatabaseConfiguration.GetSchema(builder.Configuration);

        return builder.UsePostgreSql(connectionString);
    }

    /// <summary>
    /// Configures PostgreSQL as the database provider for Beacon, in the <c>beacon</c> schema.
    /// This method is chainable after AddBeaconServices().
    /// </summary>
    public static IServiceCollection UsePostgreSql(this BeaconBuilder builder, string connectionString)
    {
        // The schema comes first so Beacon's own unqualified names resolve there and never fall through to a
        // host table. public follows because a database that already has pgvector usually has it there:
        // AddMcpEmbedding's CREATE EXTENSION IF NOT EXISTS is then a no-op, and its unqualified vector(384) /
        // vector_cosine_ops (plus the runtime ::vector / <=>) resolve only through the search path. On a
        // database without pgvector that migration still installs it into the schema, and a DBA can later move
        // it to public (ALTER EXTENSION vector SET SCHEMA public) without breaking Beacon.
        var dataSource = new NpgsqlDataSourceBuilder(connectionString)
        {
            ConnectionStringBuilder = { SearchPath = $"{Schema},public" }
        }.Build();

        return builder.AddPostgreSqlContext(_ => dataSource, setSearchPathOnOpen: false);
    }

    /// <summary>Beacon's schema is fixed to <c>beacon</c>: the argument is checked and otherwise ignored.</summary>
    [Obsolete("Beacon's schema is fixed to 'beacon'; use UsePostgreSql(connectionString). This overload is removed in 6.0.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IServiceCollection UsePostgreSql(this BeaconBuilder builder, string connectionString, string schema)
    {
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);

        return builder.UsePostgreSql(connectionString);
    }

    /// <summary>
    /// Configures PostgreSQL as Beacon's database over a host-owned <see cref="NpgsqlDataSource"/> — for example
    /// one that authenticates with Entra tokens and has no password in its connection string. Every Beacon
    /// connection (EF Core, migrations in <c>UseBeacon()</c>, raw SQL) is opened from it. See the factory
    /// overload for what Beacon requires of the data source.
    /// </summary>
    public static IServiceCollection UsePostgreSql(this BeaconBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        return builder.UsePostgreSql(_ => dataSource);
    }

    /// <summary>Beacon's schema is fixed to <c>beacon</c>: the argument is checked and otherwise ignored.</summary>
    [Obsolete("Beacon's schema is fixed to 'beacon'; use UsePostgreSql(dataSource). This overload is removed in 6.0.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IServiceCollection UsePostgreSql(this BeaconBuilder builder, NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);

        return builder.UsePostgreSql(_ => dataSource);
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
    /// must not use <c>Multiplexing</c>; Beacon refuses one that does the first time it builds its context, since the
    /// setting would otherwise leak into the host's own queries.
    /// </para>
    /// <para>
    /// No pgvector type mapping (<c>UseVector()</c>) is needed on the data source: Beacon never reads or binds the
    /// vector type, it casts text literals to <c>vector(384)</c> in SQL. The database does need the
    /// <c>vector</c> extension, which Beacon's migrations create if missing.
    /// </para>
    /// </summary>
    public static IServiceCollection UsePostgreSql(this BeaconBuilder builder, Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory)
    {
        ArgumentNullException.ThrowIfNull(dataSourceFactory);

        return builder.AddPostgreSqlContext(dataSourceFactory, setSearchPathOnOpen: true);
    }

    /// <summary>Beacon's schema is fixed to <c>beacon</c>: the argument is checked and otherwise ignored.</summary>
    [Obsolete("Beacon's schema is fixed to 'beacon'; use UsePostgreSql(dataSourceFactory). This overload is removed in 6.0.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IServiceCollection UsePostgreSql(
        this BeaconBuilder builder,
        Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory,
        string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);

        return builder.AddPostgreSqlContext(dataSourceFactory, setSearchPathOnOpen: true);
    }

    /// <summary>
    /// The model and migration options Beacon's PostgreSQL context uses, shared by <c>UsePostgreSql</c> and
    /// <see cref="BeaconPostgreSqlMigrator"/>: snake-case names, the <c>beacon</c> schema's history table, and the
    /// migration SQL generator that places the committed migrations' unqualified operations in <c>beacon</c>.
    /// </summary>
    internal static DbContextOptionsBuilder UseBeaconPostgreSqlModel(this DbContextOptionsBuilder options, NpgsqlDataSource dataSource)
    {
        return options
            .UseNpgsql(dataSource, x => x.MigrationsHistoryTable("__EFMigrationsHistory", Schema))
            .UseSnakeCaseNamingConvention()
            .ReplaceService<IMigrationsSqlGenerator, SchemaAwareMigrationsSqlGenerator>();
    }

    private static IServiceCollection AddPostgreSqlContext(
        this BeaconBuilder builder,
        Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory,
        bool setSearchPathOnOpen)
    {
        // The pgvector type mapping (data-source-level UseVector() on NpgsqlDataSourceBuilder,
        // plus o.UseVector() in UseNpgsql) is intentionally NOT wired here: the McpEmbedding EF
        // model has no Pgvector.Vector property (the vector(384) column is DB-managed only), and
        // enabling it cleanly requires restructuring how the data source is built. Vector queries
        // instead cast with a ::vector string literal and use the <=> operator.
        builder.Services.AddPooledDbContextFactory<PostgreSqlBeaconContext>((sp, options) =>
        {
            var dataSource = dataSourceFactory(sp);
            if (setSearchPathOnOpen)
            {
                RequireConnectionResetOnReturn(dataSource);
            }

            options.UseBeaconPostgreSqlModel(dataSource)
                .AddInterceptors(sp.GetRequiredService<ContentRetentionInterceptor>(), sp.GetRequiredService<SecurityAuditInterceptor>());

            // A host data source carries the host's search path, not Beacon's (see the factory overload).
            if (setSearchPathOnOpen)
            {
                options.AddInterceptors(new BeaconSearchPathInterceptor(Schema));
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

    /// <summary>
    /// Beacon sets its search path on every connection it opens from a host's data source and relies on Npgsql resetting
    /// the connection when it returns to the pool. With <c>No Reset On Close</c> the next host query on that physical
    /// connection would run against Beacon's schema; with <c>Multiplexing</c> the setting leaks into commands sharing it.
    /// </summary>
    internal static void RequireConnectionResetOnReturn(NpgsqlDataSource dataSource)
    {
        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        if (settings.Multiplexing)
        {
            throw new InvalidOperationException(
                "Beacon cannot share this NpgsqlDataSource: Multiplexing is on, so the search path Beacon sets on a connection would "
                + "leak into the host's commands on it. Turn Multiplexing off, or give Beacon a data source of its own.");
        }

        if (settings.NoResetOnClose)
        {
            throw new InvalidOperationException(
                "Beacon cannot share this NpgsqlDataSource: No Reset On Close is on, so the search path Beacon sets would survive the "
                + "connection's return to the pool and redirect the host's next query on it. Turn it off, or give Beacon a data "
                + "source of its own.");
        }
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
