using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// Whether a data source's SQL runs under a database-level read-only guarantee
/// (<see cref="IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement"/>): PostgreSQL and MySQL refuse a write inside
/// Beacon's read-only session; on every other SQL engine the parser gates are the only check Beacon makes, so the data
/// source must be connected with a login that can only read. Shown on the data-source pages and warned about in the log
/// when a data source is saved and when the host starts.
/// </summary>
internal static class DataSourceReadOnlyEnforcement
{
    public const string Guidance = "Beacon's read-only checks are the parser gates only, so connect it with a login that can only read";

    /// <summary>True: the engine refuses writes; false: it does not; null: the data source runs no SQL.</summary>
    public static bool? Of(IDataSourceProviderFactory providers, DataSourceType type, DatabaseEngineType? engine)
    {
        if (type is not (DataSourceType.Database or DataSourceType.BigQuery or DataSourceType.Databricks))
        {
            return null;
        }

        IDataSourceProvider? provider;
        try
        {
            provider = providers.GetProvider(type);
        }
        catch (BeaconException)
        {
            // No connector registered for the type: nothing enforces anything.
            return false;
        }

        return provider != null && provider.SupportsDatabaseReadOnlyEnforcement(engine);
    }

    /// <summary>A warning naming the data source by id when its engine does not refuse writes.</summary>
    public static void WarnIfNotEnforced(
        ILogger logger,
        IDataSourceProviderFactory providers,
        int dataSourceId,
        DataSourceType type,
        DatabaseEngineType? engine)
    {
        if (Of(providers, type, engine) != false)
        {
            return;
        }

        logger.LogWarning(
            "Data source {DataSourceId} ({DataSourceType} {DatabaseEngine}) has no database-level read-only enforcement: {Guidance}",
            dataSourceId,
            type,
            engine?.ToString() ?? "-",
            Guidance);
    }
}
