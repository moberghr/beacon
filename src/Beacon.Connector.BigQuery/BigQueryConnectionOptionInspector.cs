using System.Text.Json;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.BigQuery;

/// <summary>
/// A BigQuery connection must carry a service account key in <c>ServiceAccountJson</c> that reaches only Google (see
/// <see cref="BigQueryCredentialJson"/>): any other credential JSON is refused, and no key at all signs in with the Beacon
/// host's application default credentials. Read the way the provider reads its configuration. The host the connection
/// names is its key's <c>token_uri</c> host (<c>oauth2.googleapis.com</c>), when the key names one; the BigQuery API
/// endpoint is Google's fixed one and is not reported.
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

        var (kind, tokenHost) = BigQueryCredentialJson.Read(config.ServiceAccountJson);

        return kind switch
        {
            BigQueryCredentialKind.ServiceAccount => DataSourceConnectionOptionVerdict.Accept(tokenHost == null ? [] : [tokenHost]),
            BigQueryCredentialKind.ServiceAccountReachingOtherHosts => DataSourceConnectionOptionVerdict.Refuse(
                "ServiceAccountJson",
                $"Give a service account key whose token_uri is {BigQueryCredentialJson.GoogleTokenUri}, whose universe_domain is googleapis.com and whose other URLs are Google's",
                DataSourceConnectionOptionRisk.Unsafe,
                []),
            _ => DataSourceConnectionOptionVerdict.Refuse(
                "ServiceAccountJson",
                "Give a service account key (\"type\": \"service_account\") in ServiceAccountJson",
                DataSourceConnectionOptionRisk.Unsafe,
                [])
        };
    }
}
