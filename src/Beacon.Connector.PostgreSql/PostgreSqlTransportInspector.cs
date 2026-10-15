using Npgsql;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.PostgreSql;

/// <summary>
/// A PostgreSQL connection authenticates the server only with <c>SSL Mode=VerifyFull</c>: the certificate chains to a
/// trusted root (the system store, or the CA named by <c>Root Certificate</c>) and names the host. Npgsql's default
/// (<c>Prefer</c>) and <c>Require</c> encrypt without checking whom they talk to. Read with Npgsql's own builder.
/// </summary>
public sealed class PostgreSqlTransportInspector : IDataSourceTransportInspector
{
    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.PostgreSQL;

    public DataSourceTransportVerdict Inspect(string connectionData)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionData);
        var target = new DataSourceTransportTarget(builder.Host, builder.Port, builder.Database);

        return builder.SslMode == SslMode.VerifyFull
            ? DataSourceTransportVerdict.Pass(target)
            : DataSourceTransportVerdict.Fail("SSL Mode", "Set SSL Mode=VerifyFull (with Root Certificate naming the CA file when the server's CA is not in the system store)", target);
    }
}
