using System.Globalization;
using Snowflake.Data.Client;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Snowflake;

/// <summary>
/// The Snowflake driver options Beacon does not accept from an Admin, read with the driver's own builder:
/// <c>client_config_file</c> (a file on the Beacon host), <c>disable_saml_url_check=true</c>, a browser sign-in
/// (<c>authenticator=externalbrowser</c> or <c>oauth_authorization_code</c>), and signing in as the host:
/// <c>authenticator=workload_identity</c> or a <c>private_key_file</c> on the host (give the key with
/// <c>private_key</c>). The hosts are <c>host</c> (or the account's <c>snowflakecomputing.com</c> host, composed as the
/// driver composes it), the <c>proxyhost</c> when <c>useproxy</c> is on, an Okta <c>authenticator</c> URL's host and the
/// hosts of the OAuth token, authorization and redirect URLs.
/// </summary>
public sealed class SnowflakeConnectionOptionInspector : IDataSourceConnectionOptionInspector
{
    private static readonly string[] BrowserAuthenticators = ["externalbrowser", "oauth_authorization_code"];

    private static readonly string[] OAuthUrls = ["oauthtokenrequesturl", "oauthauthorizationurl", "oauthredirecturi"];

    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.Snowflake;

    public DataSourceConnectionOptionVerdict Inspect(string connectionData)
    {
        var builder = new SnowflakeDbConnectionStringBuilder { ConnectionString = connectionData };
        var authenticator = Value(builder, "authenticator");
        var hosts = Hosts(builder, authenticator);

        if (Value(builder, "client_config_file") != null)
        {
            return Unsafe("client_config_file", "Remove client_config_file", hosts);
        }

        if (IsTrue(Value(builder, "disable_saml_url_check")))
        {
            return Unsafe("disable_saml_url_check", "Remove disable_saml_url_check, or set it to false", hosts);
        }

        if (BrowserAuthenticators.Contains(authenticator, StringComparer.OrdinalIgnoreCase))
        {
            return Unsafe("authenticator", "Use an authenticator that signs in without a browser (snowflake, snowflake_jwt, oauth or programmatic_access_token)", hosts);
        }

        if (string.Equals(authenticator, "workload_identity", StringComparison.OrdinalIgnoreCase))
        {
            return HostCredentials("authenticator", "Use an authenticator that signs in with the login's own credentials (snowflake, snowflake_jwt, oauth or programmatic_access_token)", hosts);
        }

        if (Value(builder, "private_key_file") != null)
        {
            return HostCredentials("private_key_file", "Remove private_key_file and give the key with private_key", hosts);
        }

        return DataSourceConnectionOptionVerdict.Accept(hosts);
    }

    private static List<string>? Hosts(SnowflakeDbConnectionStringBuilder builder, string? authenticator)
    {
        var host = Value(builder, "host") ?? AccountHost(builder);
        if (host == null)
        {
            return null;
        }

        var hosts = new List<string> { host };
        var proxyHost = Value(builder, "proxyhost");
        if (proxyHost != null && IsTrue(Value(builder, "useproxy")))
        {
            hosts.Add(proxyHost);
        }

        if (Uri.TryCreate(authenticator, UriKind.Absolute, out var okta))
        {
            hosts.Add(okta.Host);
        }

        // An OAuth URL that is not an absolute URL names no host Beacon can tell.
        foreach (var key in OAuthUrls)
        {
            var url = Value(builder, key);
            if (url == null)
            {
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            hosts.Add(uri.Host);
        }

        return hosts;
    }

    // The host the driver composes from the account when no host is set: "_" becomes "-" unless allowunderscoresinhost.
    private static string? AccountHost(SnowflakeDbConnectionStringBuilder builder)
    {
        var account = Value(builder, "account");
        if (account == null)
        {
            return null;
        }

        var name = IsTrue(Value(builder, "allowunderscoresinhost")) ? account : account.Replace('_', '-');

        return $"{name}.snowflakecomputing.com";
    }

    private static bool IsTrue(string? value)
    {
        return value != null && bool.TryParse(value, out var flag) && flag;
    }

    private static string? Value(SnowflakeDbConnectionStringBuilder builder, string key)
    {
        var value = builder.TryGetValue(key, out var raw) && raw != null ? Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim() : null;

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static DataSourceConnectionOptionVerdict Unsafe(string option, string remedy, List<string>? hosts)
    {
        return DataSourceConnectionOptionVerdict.Refuse(option, remedy, DataSourceConnectionOptionRisk.Unsafe, hosts);
    }

    private static DataSourceConnectionOptionVerdict HostCredentials(string option, string remedy, List<string>? hosts)
    {
        return DataSourceConnectionOptionVerdict.Refuse(option, remedy, DataSourceConnectionOptionRisk.HostCredentials, hosts);
    }
}
