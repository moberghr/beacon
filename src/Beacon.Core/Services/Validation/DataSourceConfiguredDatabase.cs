using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// The database a data source's connection is configured for — the catalog on Databricks, the project on BigQuery — which
/// is the only one its SQL may name in a qualifier (<see cref="SqlCrossDatabaseRule"/>). Read from the connection string
/// the driver uses (SQL Server and Azure Synapse <c>Initial Catalog</c> / <c>Database</c>, PostgreSQL <c>Database</c> /
/// <c>DB</c>, MySQL <c>Database</c> / <c>Initial Catalog</c>, Snowflake <c>db</c>) or from the connector's settings
/// (Databricks <c>Catalog</c>, BigQuery <c>ProjectId</c>); a SQLite file is always attached as <c>main</c>. Null when
/// none is configured, and <see cref="Undetermined"/> when the connection data is missing or cannot be parsed: both
/// allow no qualifier at all (fail closed), and the refusal says which applies. A database's own default (PostgreSQL's
/// user-named database, a SQL Server login's default database, a Databricks workspace's default catalog) is not assumed.
/// </summary>
public static class DataSourceConfiguredDatabase
{
    /// <summary>
    /// Stands in for the configured database when the connection data could not be read or parsed. It never matches a
    /// qualifier; <see cref="SqlCrossDatabaseRule"/> refuses any qualifier with "the data source's database could not be
    /// determined". Not a name an engine accepts, and never shown.
    /// </summary>
    public const string Undetermined = "\0undetermined";

    /// <summary>The schema name a SQLite connection gives its own database file.</summary>
    public const string SqliteMain = "main";

    private static readonly string[] SqlServerKeys = ["Initial Catalog", "Database"];
    private static readonly string[] PostgreSqlKeys = ["Database", "DB"];
    private static readonly string[] MySqlKeys = ["Database", "Initial Catalog"];
    private static readonly string[] SnowflakeKeys = ["db"];

    /// <param name="connectionData">The decrypted connection data: a connection string, or the connector's JSON settings.</param>
    public static string? Of(DataSourceType dataSourceType, DatabaseEngineType? engineType, string? connectionData)
    {
        if (dataSourceType is not (DataSourceType.Database or DataSourceType.BigQuery or DataSourceType.Databricks))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(connectionData))
        {
            return Undetermined;
        }

        return dataSourceType switch
        {
            DataSourceType.Database when engineType == DatabaseEngineType.SQLite => FromSqlite(connectionData),
            DataSourceType.Database => FromConnectionString(connectionData, KeysOf(engineType)),
            DataSourceType.BigQuery => FromSettings(connectionData, "ProjectId"),
            _ => FromSettings(connectionData, "Catalog")
        };
    }

    /// <summary>True when <paramref name="configuredDatabase"/> is <see cref="Undetermined"/>.</summary>
    public static bool IsUndetermined(string? configuredDatabase)
    {
        return string.Equals(configuredDatabase, Undetermined, StringComparison.Ordinal);
    }

    private static string[] KeysOf(DatabaseEngineType? engineType)
    {
        return engineType switch
        {
            DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse => SqlServerKeys,
            DatabaseEngineType.PostgreSQL => PostgreSqlKeys,
            DatabaseEngineType.MySQL => MySqlKeys,
            DatabaseEngineType.Snowflake => SnowflakeKeys,
            _ => []
        };
    }

    private static string? FromConnectionString(string connectionString, string[] keys)
    {
        if (keys.Length == 0)
        {
            return null;
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in keys)
            {
                if (builder.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            return null;
        }
        catch (ArgumentException)
        {
            return Undetermined;
        }
    }

    // Whatever file the connection opens is attached as main; the connection string only has to be one.
    private static string FromSqlite(string connectionString)
    {
        try
        {
            _ = new DbConnectionStringBuilder { ConnectionString = connectionString };

            return SqliteMain;
        }
        catch (ArgumentException)
        {
            return Undetermined;
        }
    }

    // The connectors read their settings with System.Text.Json's defaults, so property names match exactly.
    private static string? FromSettings(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Undetermined;
            }

            var value = root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (JsonException)
        {
            return Undetermined;
        }
    }
}

/// <summary>
/// Resolves <see cref="DataSourceConfiguredDatabase"/> for a stored data source, decrypting its connection data (a host
/// data source's from the host's configuration). The one way Beacon reads it. Registered with <c>TryAdd</c>.
/// </summary>
public interface IConfiguredDatabaseResolver
{
    /// <summary>
    /// The configured database (catalog, project); null when none is configured, and
    /// <see cref="DataSourceConfiguredDatabase.Undetermined"/> when the connection data cannot be read or parsed.
    /// </summary>
    string? Resolve(DataSource dataSource);
}

internal sealed class ConfiguredDatabaseResolver(
    IDataSourceConnectionResolver connectionResolver,
    ILogger<ConfiguredDatabaseResolver> logger) : IConfiguredDatabaseResolver
{
    public string? Resolve(DataSource dataSource)
    {
        string connectionData;
        try
        {
            connectionData = connectionResolver.GetConnectionString(dataSource);
        }
        catch (Exception ex) when (ex is not DataSourceTransportException and not DataSourceDisabledException)
        {
            // No qualifier is allowed then; the source's execution fails on the same read anyway. A connection refused for
            // its transport, or because the source is disabled, stays that refusal, never a cross-database verdict.
            logger.LogWarning(
                "The configured database of data source {DataSourceId} could not be read ({ExceptionType}); its SQL may not name a database.",
                dataSource.Id,
                ex.GetType().Name);
            return DataSourceConfiguredDatabase.Undetermined;
        }

        var configured = DataSourceConfiguredDatabase.Of(dataSource.DataSourceType, dataSource.DatabaseEngineType, connectionData);
        if (DataSourceConfiguredDatabase.IsUndetermined(configured))
        {
            // Identifiers only: the connection data holds credentials (§1.11).
            logger.LogWarning(
                "The connection data of data source {DataSourceId} could not be parsed, so its configured database could not be determined; its SQL may not name a database.",
                dataSource.Id);
        }

        return configured;
    }
}
