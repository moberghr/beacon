using System.Collections.Concurrent;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// What Beacon does with a data-source connection that does not authenticate the server over TLS, as its connector's
/// <see cref="IDataSourceTransportInspector"/> judges it (a type or engine without one is never verified). A new or changed
/// connection (create, update, test connection) is refused; a stored one keeps working with one Warning per data source
/// per process, or is refused under <c>Beacon:DataSources:Transport:EnforceOnExisting</c>.
/// <c>Beacon:DataSources:Transport:AllowInsecure</c> lifts both. A refusal is a <see cref="DataSourceTransportException"/>
/// naming the option to change; neither a refusal nor a Warning carries a value read from the connection data (§1.11).
/// Registered with <c>TryAdd</c> as a singleton.
/// </summary>
public interface IDataSourceTransportPolicy
{
    /// <summary>The verdict on the connection data, without refusing or warning; the stored source's badge.</summary>
    DataSourceTransportVerdict Inspect(DataSourceType dataSourceType, DatabaseEngineType? databaseEngineType, string connectionData);

    /// <summary>Refuses a new or changed connection that fails the check (unless the check is lifted).</summary>
    void EnsureNewConnectionAllowed(DataSourceType dataSourceType, DatabaseEngineType? databaseEngineType, string connectionData);

    /// <summary>
    /// A stored data source about to connect with <paramref name="connectionData"/> (a host-managed source with the string
    /// the host configures): a failing check is one Warning per data source per process, or a refusal under
    /// <c>EnforceOnExisting</c>. A source not saved yet (no id) is a new connection.
    /// </summary>
    void EnsureStoredConnectionAllowed(DataSource dataSource, string connectionData);
}

internal sealed class DataSourceTransportPolicy(
    IEnumerable<IDataSourceTransportInspector> inspectors,
    IOptions<DataSourceTransportOptions> options,
    ILogger<DataSourceTransportPolicy> logger) : IDataSourceTransportPolicy
{
    private const string NoInspectorRemedy = "No transport check is registered for this data source type, so Beacon cannot verify that its connection authenticates the server";

    private readonly List<IDataSourceTransportInspector> _inspectors = inspectors.ToList();

    private readonly ConcurrentDictionary<int, byte> _warned = new();

    public DataSourceTransportVerdict Inspect(DataSourceType dataSourceType, DatabaseEngineType? databaseEngineType, string connectionData)
    {
        var engine = dataSourceType == DataSourceType.Database ? databaseEngineType : null;
        var inspector = _inspectors
            .Where(x => x.DataSourceType == dataSourceType)
            .Where(x => x.DatabaseEngineType == engine)
            .LastOrDefault();
        if (inspector == null)
        {
            return DataSourceTransportVerdict.Fail("data source type", NoInspectorRemedy);
        }

        if (string.IsNullOrWhiteSpace(connectionData))
        {
            return DataSourceTransportVerdict.Unreadable();
        }

        try
        {
            return inspector.Inspect(connectionData);
        }
        catch (Exception)
        {
            // A parser's message can quote the connection data: only the fact that it could not be read is kept.
            return DataSourceTransportVerdict.Unreadable();
        }
    }

    public void EnsureNewConnectionAllowed(DataSourceType dataSourceType, DatabaseEngineType? databaseEngineType, string connectionData)
    {
        if (options.Value.AllowInsecure)
        {
            return;
        }

        var verdict = Inspect(dataSourceType, databaseEngineType, connectionData);
        if (!verdict.Verified)
        {
            throw Refusal(verdict);
        }
    }

    public void EnsureStoredConnectionAllowed(DataSource dataSource, string connectionData)
    {
        if (dataSource.Id <= 0)
        {
            EnsureNewConnectionAllowed(dataSource.DataSourceType, dataSource.DatabaseEngineType, connectionData);
            return;
        }

        if (options.Value.AllowInsecure)
        {
            return;
        }

        var verdict = Inspect(dataSource.DataSourceType, dataSource.DatabaseEngineType, connectionData);
        if (verdict.Verified)
        {
            return;
        }

        if (options.Value.EnforceOnExisting)
        {
            logger.LogWarning(
                "Data source {DataSourceId} was refused: its connection does not authenticate the server over TLS ({Option}; {Section}:{Setting} is on)",
                dataSource.Id,
                verdict.Option,
                DataSourceTransportOptions.SectionName,
                nameof(DataSourceTransportOptions.EnforceOnExisting));

            throw Refusal(verdict);
        }

        if (_warned.TryAdd(dataSource.Id, 0))
        {
            logger.LogWarning(
                "Data source {DataSourceId} connects without authenticating the server over TLS ({Option}). It keeps working until its connection is replaced; {Section}:{Setting} refuses it",
                dataSource.Id,
                verdict.Option,
                DataSourceTransportOptions.SectionName,
                nameof(DataSourceTransportOptions.EnforceOnExisting));
        }
    }

    private static DataSourceTransportException Refusal(DataSourceTransportVerdict verdict)
    {
        return new DataSourceTransportException(
            $"Beacon requires a data-source connection that authenticates the server over TLS. {verdict.Remedy}.");
    }
}
