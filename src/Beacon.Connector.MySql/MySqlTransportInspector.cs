using MySql.Data.MySqlClient;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.MySql;

/// <summary>
/// A MySQL connection authenticates the server only with <c>SslMode=VerifyFull</c>: the certificate chains to a trusted
/// root (the system store, or the CA file named by <c>SslCa</c>) and names the host. The driver's default
/// (<c>Preferred</c>), <c>Required</c> and <c>VerifyCA</c> do not check the host. Read with MySql.Data's own builder, which
/// also gives the connection's definition: its settings under MySql.Data's canonical names, without the passwords (the
/// multi-factor ones too), the client certificate's password and the OpenID identity token; the login is the user name,
/// or the authentication plugin (or integrated security) when none is given.
/// </summary>
public sealed class MySqlTransportInspector : IDataSourceTransportInspector
{
    private const string IntegratedLogin = "Integrated Security";

    private static readonly string[] SecretKeys = ["password", "password2", "password3", "certificatepassword", "openididentitytoken"];

    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.MySQL;

    public DataSourceTransportVerdict Inspect(string connectionData)
    {
        var builder = new MySqlConnectionStringBuilder(connectionData);
        var target = new DataSourceTransportTarget(builder.Server, (int)builder.Port, builder.Database);
        var definition = DataSourceConnectionDefinition.FromBuilder(builder, new MySqlConnectionStringBuilder(), SecretKeys, Login(builder));
        var verdict = builder.SslMode == MySqlSslMode.VerifyFull
            ? DataSourceTransportVerdict.Pass(target)
            : DataSourceTransportVerdict.Fail("SslMode", "Set SslMode=VerifyFull (with SslCa naming the CA file when the server's CA is not in the system store)", target);

        return verdict.WithDefinition(definition);
    }

    private static string? Login(MySqlConnectionStringBuilder builder)
    {
        if (!string.IsNullOrEmpty(builder.UserID))
        {
            return builder.UserID;
        }

        if (!string.IsNullOrEmpty(builder.DefaultAuthenticationPlugin))
        {
            return builder.DefaultAuthenticationPlugin;
        }

        return builder.IntegratedSecurity ? IntegratedLogin : null;
    }
}
