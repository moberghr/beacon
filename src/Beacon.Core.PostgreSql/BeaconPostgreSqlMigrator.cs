using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Beacon.Core.Data;
using Beacon.Core.PostgreSql.Data;

namespace Beacon.Core.PostgreSql;

/// <summary>
/// Applies Beacon's schema on PostgreSQL without composing Beacon: for a host's migration process (a migrator job, a
/// deploy step) that shares the application's database but not its service container, where
/// <c>Beacon.Core.ServiceConfiguration.UseBeacon</c> would need Beacon's whole application layer registered. Creates the
/// schema when missing and applies the pending migrations; idempotent. One migration installs the <c>vector</c> extension,
/// so the server must allow it (on Azure Database for PostgreSQL, <c>VECTOR</c> in <c>azure.extensions</c>).
/// </summary>
public static class BeaconPostgreSqlMigrator
{
    /// <summary>
    /// Migrates over a host-owned data source (one that authenticates with Entra tokens, say), setting Beacon's search
    /// path on each connection as the web application does. The data source must reset connections on return to the pool
    /// and must not multiplex, or the search path would leak into the host's own work on it.
    /// </summary>
    public static async Task MigrateAsync(
        NpgsqlDataSource dataSource,
        string schema = BeaconDatabaseConfiguration.DefaultSchema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);
        ServiceCollectionExtensions.RequireConnectionResetOnReturn(dataSource);

        var options = Options(new DbContextOptionsBuilder<PostgreSqlBeaconContext>().UseNpgsql(dataSource, x => x.MigrationsHistoryTable("__EFMigrationsHistory", schema)), schema)
            .AddInterceptors(new BeaconSearchPathInterceptor(schema))
            .Options;

        await using var context = new PostgreSqlBeaconContext(options);
        await BeaconSchema.MigrateAsync(context, createSchema: true, cancellationToken);
    }

    /// <summary>Migrates over a connection string, with Beacon's search path in it, as <c>UsePostgreSql(connectionString)</c> connects.</summary>
    public static async Task MigrateAsync(
        string connectionString,
        string schema = BeaconDatabaseConfiguration.DefaultSchema,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);

        await using var dataSource = new NpgsqlDataSourceBuilder(connectionString)
        {
            ConnectionStringBuilder = { SearchPath = $"{schema},public" }
        }.Build();
        var options = Options(new DbContextOptionsBuilder<PostgreSqlBeaconContext>().UseNpgsql(dataSource, x => x.MigrationsHistoryTable("__EFMigrationsHistory", schema)), schema)
            .Options;

        await using var context = new PostgreSqlBeaconContext(options);
        await BeaconSchema.MigrateAsync(context, createSchema: true, cancellationToken);
    }

    // The model options the web application's context uses (UsePostgreSql), without its save-time interceptors.
    private static DbContextOptionsBuilder<PostgreSqlBeaconContext> Options(DbContextOptionsBuilder<PostgreSqlBeaconContext> builder, string schema)
    {
        builder
            .UseSnakeCaseNamingConvention()
            .UseBeaconSchema(schema)
            .ReplaceService<IMigrationsSqlGenerator, SchemaAwareMigrationsSqlGenerator>()
            .ReplaceService<IModelCacheKeyFactory, BeaconSchemaModelCacheKeyFactory>();

        return builder;
    }
}
