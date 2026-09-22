using System;
using Microsoft.Extensions.Configuration;

namespace Beacon.Core.Data;

/// <summary>
/// Resolves Beacon's own data-store settings from configuration, so a host can chain
/// <c>.UsePostgreSql()</c> / <c>.UseSqlServer()</c> without threading the connection string
/// through <c>Program.cs</c> positionally.
///
/// Beacon runs in the <c>beacon</c> schema and only that schema. The migration snapshot bakes the
/// literal in, so EF compares model against snapshot and raises PendingModelChangesWarning on any
/// other value — which makes <c>UseBeacon()</c>'s <c>Database.Migrate()</c> throw before it applies
/// anything. Adding a migration cannot fix that, because the next snapshot bakes one schema too.
/// Rather than let a host discover this as an opaque EF error at boot, <c>Beacon:Schema</c> is
/// rejected up front with a message that says what to do.
/// </summary>
public static class BeaconDatabaseConfiguration
{
    public const string ConnectionStringName = "BeaconContext";

    public const string SchemaKey = "Beacon:Schema";

    /// <summary>The one schema Beacon supports. Not configurable — see the type-level remarks.</summary>
    public const string DefaultSchema = "beacon";

    public static string GetConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. Set " +
                $"ConnectionStrings:{ConnectionStringName} via appsettings, user-secrets, or an " +
                "environment variable, or pass the connection string to UsePostgreSql/UseSqlServer explicitly.");
        }

        return connectionString;
    }

    public static string GetSchema(IConfiguration configuration)
    {
        var schema = configuration[SchemaKey];

        if (string.IsNullOrWhiteSpace(schema))
        {
            return DefaultSchema;
        }

        return RequireSupportedSchema(schema);
    }

    /// <summary>
    /// Accepts only <see cref="DefaultSchema"/>. Throws with actionable guidance otherwise, so a
    /// host is told to change its configuration instead of hitting PendingModelChangesWarning.
    /// </summary>
    public static string RequireSupportedSchema(string schema)
    {
        BeaconSchema.ValidateIdentifier(schema);

        if (!string.Equals(schema, DefaultSchema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Beacon only supports the '{DefaultSchema}' schema, but '{schema}' was configured. "
                + $"Remove {SchemaKey} (or set it to '{DefaultSchema}') and move any existing Beacon "
                + $"tables into '{DefaultSchema}'. Beacon's migrations bake the schema into their "
                + "snapshot, so running in another schema makes Database.Migrate() fail with "
                + "PendingModelChangesWarning and no migration can resolve it.");
        }

        return schema;
    }
}
