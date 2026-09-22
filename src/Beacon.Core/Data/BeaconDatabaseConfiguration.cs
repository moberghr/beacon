using System;
using Microsoft.Extensions.Configuration;

namespace Beacon.Core.Data;

/// <summary>
/// Resolves Beacon's own data-store settings from configuration — <c>ConnectionStrings:BeaconContext</c>
/// and <c>Beacon:Schema</c> — so a host can chain <c>.UsePostgreSql()</c> / <c>.UseSqlServer()</c>
/// without threading the connection string and schema through <c>Program.cs</c> positionally.
/// </summary>
public static class BeaconDatabaseConfiguration
{
    public const string ConnectionStringName = "BeaconContext";

    public const string SchemaKey = "Beacon:Schema";

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

        return BeaconSchema.ValidateIdentifier(string.IsNullOrWhiteSpace(schema) ? DefaultSchema : schema);
    }
}
