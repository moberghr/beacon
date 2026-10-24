using System.Globalization;
using Snowflake.Data.Client;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Snowflake;

/// <summary>
/// The Snowflake driver always connects over HTTPS and checks the server's certificate, unless the connection string
/// turns a check off: <c>insecureMode=true</c> (the certificate revocation check), a <c>certRevocationCheckMode</c> other
/// than <c>ENABLED</c> (a revocation check that does not refuse a revoked certificate), or a <c>scheme</c> other than
/// https. None is accepted. Read with the driver's own builder (option names in any case), which also gives the
/// connection's definition: every setting under its lowercase name (the driver's names are case-insensitive and the builder
/// keeps any name, so a setting the driver does not know is kept), the driver's defaults left out, without the password,
/// passcode, private key and its password, token, proxy password and OAuth client secret; the login is the user, or the
/// authenticator when none is given.
/// </summary>
public sealed class SnowflakeTransportInspector : IDataSourceTransportInspector
{
    private const string DefaultAuthenticator = "snowflake";

    private static readonly string[] SecretKeys = ["password", "passcode", "private_key", "private_key_pwd", "token", "proxypassword", "oauthclientsecret"];

    // The values the driver takes for a setting left out; the same value typed out is left out of the definition.
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["authenticator"] = DefaultAuthenticator,
        ["insecuremode"] = "false",
        ["port"] = "443",
        ["scheme"] = "https"
    };

    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.Snowflake;

    public DataSourceTransportVerdict Inspect(string connectionData)
    {
        var builder = new SnowflakeDbConnectionStringBuilder { ConnectionString = connectionData };
        var target = new DataSourceTransportTarget(Value(builder, "host") ?? Value(builder, "account"), Port(builder), Value(builder, "db"));

        return Verdict(builder, target).WithDefinition(Definition(builder));
    }

    private static DataSourceTransportVerdict Verdict(SnowflakeDbConnectionStringBuilder builder, DataSourceTransportTarget target)
    {
        var insecureMode = Value(builder, "insecureMode");
        if (insecureMode != null && !(bool.TryParse(insecureMode, out var insecure) && !insecure))
        {
            return DataSourceTransportVerdict.Fail("insecureMode", "Remove insecureMode, or set insecureMode=false", target);
        }

        var revocationCheck = Value(builder, "certRevocationCheckMode");
        if (revocationCheck != null && !string.Equals(revocationCheck, "ENABLED", StringComparison.OrdinalIgnoreCase))
        {
            return DataSourceTransportVerdict.Fail("certRevocationCheckMode", "Remove certRevocationCheckMode, or set certRevocationCheckMode=ENABLED", target);
        }

        var scheme = Value(builder, "scheme");
        return scheme != null && !string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
            ? DataSourceTransportVerdict.Fail("scheme", "Remove scheme, or set scheme=https", target)
            : DataSourceTransportVerdict.Pass(target);
    }

    private static DataSourceConnectionDefinition Definition(SnowflakeDbConnectionStringBuilder builder)
    {
        var settings = builder.Keys
            .Cast<string>()
            .Select(x => new KeyValuePair<string, string?>(x.ToLowerInvariant(), Value(builder, x)))
            .Where(x => !(Defaults.TryGetValue(x.Key, out var unset) && string.Equals(unset, x.Value, StringComparison.OrdinalIgnoreCase)));
        var login = Value(builder, "user") is { Length: > 0 } user ? user : Value(builder, "authenticator") ?? DefaultAuthenticator;

        return DataSourceConnectionDefinition.Create(settings, SecretKeys, login);
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
