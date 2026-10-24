using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
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
    private const string Schema = BeaconDatabaseConfiguration.DefaultSchema;

    /// <summary>
    /// Migrates over a host-owned data source (one that authenticates with Entra tokens, say), setting Beacon's search
    /// path on each connection as the web application does. The data source must reset connections on return to the pool
    /// and must not multiplex, or the search path would leak into the host's own work on it.
    /// </summary>
    public static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ServiceCollectionExtensions.RequireConnectionResetOnReturn(dataSource);

        await using var context = new PostgreSqlBeaconContext(ContextOptions(dataSource, setSearchPathOnOpen: true));
        await BeaconSchema.MigrateAsync(context, createSchema: true, cancellationToken);
    }

    /// <summary>Beacon's schema is fixed to <c>beacon</c>: the argument is checked and otherwise ignored.</summary>
    [Obsolete("Beacon's schema is fixed to 'beacon'; use MigrateAsync(dataSource, cancellationToken). This overload is removed in 6.0.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static async Task MigrateAsync(NpgsqlDataSource dataSource, string schema, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);

        await MigrateAsync(dataSource, cancellationToken);
    }

    /// <summary>Migrates over a connection string, with Beacon's search path in it, as <c>UsePostgreSql(connectionString)</c> connects.</summary>
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var dataSource = new NpgsqlDataSourceBuilder(connectionString)
        {
            ConnectionStringBuilder = { SearchPath = $"{Schema},public" }
        }.Build();

        await using var context = new PostgreSqlBeaconContext(ContextOptions(dataSource, setSearchPathOnOpen: false));
        await BeaconSchema.MigrateAsync(context, createSchema: true, cancellationToken);
    }

    /// <summary>Beacon's schema is fixed to <c>beacon</c>: the argument is checked and otherwise ignored.</summary>
    [Obsolete("Beacon's schema is fixed to 'beacon'; use MigrateAsync(connectionString, cancellationToken). This overload is removed in 6.0.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static async Task MigrateAsync(string connectionString, string schema, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);

        await MigrateAsync(connectionString, cancellationToken);
    }

    // The model options the web application's context uses (UsePostgreSql), without its save-time interceptors.
    internal static DbContextOptions<PostgreSqlBeaconContext> ContextOptions(NpgsqlDataSource dataSource, bool setSearchPathOnOpen)
    {
        var builder = new DbContextOptionsBuilder<PostgreSqlBeaconContext>();
        builder.UseBeaconPostgreSqlModel(dataSource);
        if (setSearchPathOnOpen)
        {
            builder.AddInterceptors(new BeaconSearchPathInterceptor(Schema));
        }

        return builder.Options;
    }
}
