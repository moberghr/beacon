using Npgsql;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.PostgreSql;

/// <summary>
/// A PostgreSQL connection authenticates the server only with <c>SSL Mode=VerifyFull</c>: the certificate chains to a
/// trusted root (the system store, or the CA named by <c>Root Certificate</c>) and names the host. Npgsql's default
/// (<c>Prefer</c>) and <c>Require</c> encrypt without checking whom they talk to. Read with Npgsql's own builder, which
/// also gives the connection's definition: its settings under Npgsql's canonical names, without the password and the
/// client key's password; the login is the user name, or integrated security (Kerberos/GSS) when none is given.
/// </summary>
public sealed class PostgreSqlTransportInspector : IDataSourceTransportInspector
{
    private const string IntegratedLogin = "Integrated Security";

    private static readonly string[] SecretKeys = ["Password", "SSL Password"];

    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.PostgreSQL;

    public DataSourceTransportVerdict Inspect(string connectionData)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionData);
        var target = new DataSourceTransportTarget(builder.Host, builder.Port, builder.Database);
        var definition = DataSourceConnectionDefinition.FromBuilder(builder, new NpgsqlConnectionStringBuilder(), SecretKeys, Login(builder));
        var verdict = builder.SslMode == SslMode.VerifyFull
            ? DataSourceTransportVerdict.Pass(target)
            : DataSourceTransportVerdict.Fail("SSL Mode", "Set SSL Mode=VerifyFull (with Root Certificate naming the CA file when the server's CA is not in the system store)", target);

        return verdict.WithDefinition(definition);
    }

    private static string Login(NpgsqlConnectionStringBuilder builder)
    {
        return string.IsNullOrEmpty(builder.Username) ? IntegratedLogin : builder.Username;
    }
}
