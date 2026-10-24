using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Databricks;

/// <summary>
/// A Databricks connection's <c>Host</c> is the workspace's host name alone (such as
/// <c>adb-1234567890123456.7.azuredatabricks.net</c>): the provider builds every request URL from it, so a scheme, port,
/// path, query or user part is refused. Read the way the provider reads its configuration. The host is the connection's
/// only host.
/// </summary>
public sealed partial class DatabricksConnectionOptionInspector : IDataSourceConnectionOptionInspector
{
    public DataSourceType DataSourceType => DataSourceType.Databricks;

    public DatabaseEngineType? DatabaseEngineType => null;

    public DataSourceConnectionOptionVerdict Inspect(string connectionData)
    {
        var config = JsonSerializer.Deserialize<DatabricksConfiguration>(connectionData);
        if (config == null)
        {
            return DataSourceConnectionOptionVerdict.NotReadable();
        }

        var host = config.Host?.Trim() ?? string.Empty;

        return HostName().IsMatch(host)
            ? DataSourceConnectionOptionVerdict.Accept([host])
            : DataSourceConnectionOptionVerdict.Refuse(
                "Host",
                "Set Host to the workspace's host name alone, without https://, a port or a path",
                DataSourceConnectionOptionRisk.Unsafe);
    }

    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HostName();
}
