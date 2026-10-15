using MySql.Data.MySqlClient;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.MySql;

/// <summary>
/// A MySQL connection authenticates the server only with <c>SslMode=VerifyFull</c>: the certificate chains to a trusted
/// root (the system store, or the CA file named by <c>SslCa</c>) and names the host. The driver's default
/// (<c>Preferred</c>), <c>Required</c> and <c>VerifyCA</c> do not check the host. Read with MySql.Data's own builder.
/// </summary>
public sealed class MySqlTransportInspector : IDataSourceTransportInspector
{
    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.MySQL;

    public DataSourceTransportVerdict Inspect(string connectionData)
    {
        var builder = new MySqlConnectionStringBuilder(connectionData);
        var target = new DataSourceTransportTarget(builder.Server, (int)builder.Port, builder.Database);

        return builder.SslMode == MySqlSslMode.VerifyFull
            ? DataSourceTransportVerdict.Pass(target)
            : DataSourceTransportVerdict.Fail("SslMode", "Set SslMode=VerifyFull (with SslCa naming the CA file when the server's CA is not in the system store)", target);
    }
}
