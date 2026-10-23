using System.Text.Json;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.BigQuery;

/// <summary>
/// A BigQuery connection must carry a service account key in <c>ServiceAccountJson</c>: any other credential JSON is
/// refused, and no key at all signs in with the Beacon host's application default credentials. Read the way the provider
/// reads its configuration. The client reaches only Google's own endpoints, so the connection names no host.
/// </summary>
public sealed class BigQueryConnectionOptionInspector : IDataSourceConnectionOptionInspector
{
    public DataSourceType DataSourceType => DataSourceType.BigQuery;

    public DatabaseEngineType? DatabaseEngineType => null;

    public DataSourceConnectionOptionVerdict Inspect(string connectionData)
    {
        var config = JsonSerializer.Deserialize<BigQueryConfiguration>(connectionData);
        if (config == null)
        {
            return DataSourceConnectionOptionVerdict.NotReadable();
        }

        if (string.IsNullOrWhiteSpace(config.ServiceAccountJson))
        {
            return DataSourceConnectionOptionVerdict.Refuse(
                "ServiceAccountJson",
                "Give the service account key in ServiceAccountJson",
                DataSourceConnectionOptionRisk.HostCredentials,
                []);
        }

        return BigQueryCredentialJson.IsServiceAccount(config.ServiceAccountJson)
            ? DataSourceConnectionOptionVerdict.Accept([])
            : DataSourceConnectionOptionVerdict.Refuse(
                "ServiceAccountJson",
                "Give a service account key (\"type\": \"service_account\") in ServiceAccountJson",
                DataSourceConnectionOptionRisk.Unsafe,
                []);
    }
}
