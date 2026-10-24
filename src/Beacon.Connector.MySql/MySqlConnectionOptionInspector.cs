using System.Text.RegularExpressions;
using MySql.Data.MySqlClient;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.MySql;

/// <summary>
/// The MySql.Data options Beacon does not accept from an Admin, read with MySql.Data's own builder:
/// <c>AllowLoadLocalInfile</c> and <c>AllowLoadLocalInfileInPath</c> (the server may then read files on the Beacon host),
/// <c>CommandInterceptors</c> and <c>ExceptionInterceptors</c> (types loaded by name), and signing in as the host:
/// <c>IntegratedSecurity</c>, a Kerberos, Windows or OCI authentication plugin, <c>OciConfigFile</c>, a client certificate
/// from the host's <c>CertificateStoreLocation</c>, a named pipe, shared memory or Unix socket <c>ConnectionProtocol</c>,
/// or no <c>Password</c>. MySql.Data follows a server's switch to another authentication plugin and has no setting to
/// refuse one: with a password the Kerberos plugin signs in with the login's own credentials and the OCI plugin needs the
/// OCI SDK, which Beacon does not ship, but on a Windows host the Windows plugin signs in as the host's Windows account,
/// so a MySQL server Beacon connects to from Windows must be trusted not to ask for it. The hosts are every host
/// <c>Server</c> lists; with <c>DnsSrv</c> the hosts come from DNS and cannot be told.
/// </summary>
public sealed partial class MySqlConnectionOptionInspector : IDataSourceConnectionOptionInspector
{
    private static readonly string[] HostCredentialPlugins = ["authentication_kerberos_client", "authentication_windows_client", "authentication_oci_client"];

    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.MySQL;

    public DataSourceConnectionOptionVerdict Inspect(string connectionData)
    {
        var builder = new MySqlConnectionStringBuilder(connectionData);
        var hosts = builder.DnsSrv ? null : Hosts(builder.Server);

        if (builder.AllowLoadLocalInfile)
        {
            return Unsafe("AllowLoadLocalInfile", "Remove AllowLoadLocalInfile, or set it to false", hosts);
        }

        if (!string.IsNullOrEmpty(builder.AllowLoadLocalInfileInPath))
        {
            return Unsafe("AllowLoadLocalInfileInPath", "Remove AllowLoadLocalInfileInPath", hosts);
        }

        if (!string.IsNullOrEmpty(builder.CommandInterceptors))
        {
            return Unsafe("CommandInterceptors", "Remove CommandInterceptors", hosts);
        }

        if (!string.IsNullOrEmpty(builder.ExceptionInterceptors))
        {
            return Unsafe("ExceptionInterceptors", "Remove ExceptionInterceptors", hosts);
        }

        if (builder.IntegratedSecurity)
        {
            return HostCredentials("IntegratedSecurity", "Remove IntegratedSecurity and sign in with the login's user name and password", hosts);
        }

        if (HostCredentialPlugins.Contains(builder.DefaultAuthenticationPlugin?.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return HostCredentials("DefaultAuthenticationPlugin", "Remove DefaultAuthenticationPlugin, or use a plugin that signs in with the login's own password", hosts);
        }

        if (!string.IsNullOrEmpty(builder.OciConfigFile))
        {
            return HostCredentials("OciConfigFile", "Remove OciConfigFile and sign in with the login's user name and password", hosts);
        }

        if (builder.CertificateStoreLocation != MySqlCertificateStoreLocation.None)
        {
            return HostCredentials("CertificateStoreLocation", "Remove CertificateStoreLocation (use CertificateFile for a client certificate)", hosts);
        }

        if (builder.ConnectionProtocol is not (MySqlConnectionProtocol.Sockets or MySqlConnectionProtocol.Tcp))
        {
            return HostCredentials("ConnectionProtocol", "Remove ConnectionProtocol, or set it to Tcp, and connect to the server's host name", hosts);
        }

        // MySql.Data follows a server that switches the sign-in to its Kerberos plugin, which without a password signs in
        // with the Beacon host's Kerberos tickets; the driver offers no way to refuse the switch.
        if (string.IsNullOrEmpty(builder.Password))
        {
            return HostCredentials("Password", "Give the login's Password", hosts);
        }

        return DataSourceConnectionOptionVerdict.Accept(hosts);
    }

    // "host1,host2" or "(address=host1,priority=100),(address=host2,priority=50)"; null when none is named.
    private static List<string>? Hosts(string? server)
    {
        var value = server ?? string.Empty;
        var hosts = value.Contains('(')
            ? Address().Matches(value).Select(x => x.Groups[1].Value.Trim()).ToList()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var named = hosts
            .Select(x => x.Trim('[', ']'))
            .Where(x => x.Length > 0)
            .ToList();

        return named.Count == 0 ? null : named;
    }

    private static DataSourceConnectionOptionVerdict Unsafe(string option, string remedy, List<string>? hosts)
    {
        return DataSourceConnectionOptionVerdict.Refuse(option, remedy, DataSourceConnectionOptionRisk.Unsafe, hosts);
    }

    private static DataSourceConnectionOptionVerdict HostCredentials(string option, string remedy, List<string>? hosts)
    {
        return DataSourceConnectionOptionVerdict.Refuse(option, remedy, DataSourceConnectionOptionRisk.HostCredentials, hosts);
    }

    [GeneratedRegex(@"address\s*=\s*([^,)]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Address();
}
