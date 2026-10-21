using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    public static async Task MigrateAsync(
        string connectionString,
        string schema = BeaconDatabaseConfiguration.DefaultSchema,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        BeaconDatabaseConfiguration.RequireSupportedSchema(schema);

        // The model options the web application's context uses (UseSqlServer), without its save-time interceptors.
        var options = new DbContextOptionsBuilder<SqlServerBeaconContext>();
        options
            .UseSqlServer(connectionString, x => x.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .UseBeaconSchema(schema)
            .ReplaceService<IMigrationsSqlGenerator, SchemaAwareMigrationsSqlGenerator>()
            .ReplaceService<IModelCacheKeyFactory, BeaconSchemaModelCacheKeyFactory>();

        await using var context = new SqlServerBeaconContext(options.Options);
        await BeaconSchema.MigrateAsync(context, createSchema: true, cancellationToken);
    }
}
