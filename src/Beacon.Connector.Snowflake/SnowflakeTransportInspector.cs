using System.Globalization;
using Snowflake.Data.Client;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Snowflake;

/// <summary>
/// The Snowflake driver always connects over HTTPS and checks the server's certificate, unless the connection string
/// turns a check off: <c>insecureMode=true</c> (the certificate revocation check) or a <c>scheme</c> other than https.
/// Neither is accepted. Read with the driver's own builder (option names in any case).
/// </summary>
public sealed class SnowflakeTransportInspector : IDataSourceTransportInspector
{
    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.Snowflake;

    public DataSourceTransportVerdict Inspect(string connectionData)
    {
        var builder = new SnowflakeDbConnectionStringBuilder { ConnectionString = connectionData };
        var target = new DataSourceTransportTarget(Value(builder, "host") ?? Value(builder, "account"), Port(builder), Value(builder, "db"));

        var insecureMode = Value(builder, "insecureMode");
        if (insecureMode != null && !(bool.TryParse(insecureMode, out var insecure) && !insecure))
        {
            return DataSourceTransportVerdict.Fail("insecureMode", "Remove insecureMode, or set insecureMode=false", target);
        }

        var scheme = Value(builder, "scheme");
        return scheme != null && !string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
            ? DataSourceTransportVerdict.Fail("scheme", "Remove scheme, or set scheme=https", target)
            : DataSourceTransportVerdict.Pass(target);
    }

    private static string? Value(SnowflakeDbConnectionStringBuilder builder, string key)
    {
        return builder.TryGetValue(key, out var value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() : null;
    }

    private static int? Port(SnowflakeDbConnectionStringBuilder builder)
    {
        return int.TryParse(Value(builder, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null;
    }
}
