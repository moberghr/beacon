using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// The SQL dialect name every read-only check uses for a data source. A database speaks its engine's dialect; BigQuery
/// and Databricks have no engine type and speak their own. Null for a source that runs no SQL, or a database without
/// an engine type — the read-only validator refuses an unknown dialect instead of falling back to a generic grammar.
/// </summary>
public static class DataSourceSqlDialect
{
    public static string? Of(DataSource dataSource)
    {
        return Of(dataSource.DataSourceType, dataSource.DatabaseEngineType);
    }

    public static string? Of(DataSourceType dataSourceType, DatabaseEngineType? engineType)
    {
        return dataSourceType switch
        {
            DataSourceType.Database => engineType?.ToString(),
            DataSourceType.BigQuery => "bigquery",
            DataSourceType.Databricks => "databricks",
            _ => null
        };
    }
}
