using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.SqlServer.Data;

namespace Beacon.Core.SqlServer;

/// <summary>
/// Applies Beacon's schema on SQL Server without composing Beacon: for a host's migration process (a migrator job, a
/// deploy step) that shares the application's database but not its service container, where
/// <c>Beacon.Core.ServiceConfiguration.UseBeacon</c> would need Beacon's whole application layer registered. Creates the
/// schema when missing and applies the pending migrations; idempotent.
/// </summary>
public static class BeaconSqlServerMigrator
{
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var context = new SqlServerBeaconContext(ContextOptions(connectionString));
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

    // The model options the web application's context uses (UseSqlServer), without its save-time interceptors.
    internal static DbContextOptions<SqlServerBeaconContext> ContextOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<SqlServerBeaconContext>();
        builder.UseBeaconSqlServerModel(connectionString);

        return builder.Options;
    }
}
