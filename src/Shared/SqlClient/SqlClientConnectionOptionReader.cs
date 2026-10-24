using Microsoft.Data.SqlClient;
using Beacon.Core.Services.Providers;

namespace Beacon.Shared.SqlClient;

/// <summary>
/// The SqlClient options Beacon does not accept from an Admin, read with SqlClient's own builder: <c>AttachDBFilename</c>
/// and <c>User Instance</c> (a database file on the Beacon host), an interactive <c>Authentication</c> (a sign-in on the
/// server), and signing in as the host: <c>Integrated Security</c>, an <c>Authentication</c> that uses the host's
/// identity (integrated, managed, workload or default), <c>Column Encryption Setting=Enabled</c> (the host's column
/// master key stores), and a named-pipe, shared-memory or LocalDB <c>Data Source</c>. The hosts are the server of
/// <c>Data Source</c> and of <c>Failover Partner</c>. Compiled into each SqlClient connector as a linked file.
/// </summary>
internal static class SqlClientConnectionOptionReader
{
    private static readonly SqlAuthenticationMethod[] OwnCredentials =
    [
        SqlAuthenticationMethod.NotSpecified,
        SqlAuthenticationMethod.SqlPassword,
        SqlAuthenticationMethod.ActiveDirectoryPassword,
        SqlAuthenticationMethod.ActiveDirectoryServicePrincipal
    ];

    private static readonly SqlAuthenticationMethod[] Interactive =
    [
        SqlAuthenticationMethod.ActiveDirectoryInteractive,
        SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow
    ];

    private static readonly string[] LocalProtocols = ["np:", "lpc:", "(localdb)"];

    // Throws when the builder cannot read the connection string; the transport policy refuses it as unreadable.
    public static DataSourceConnectionOptionVerdict Inspect(string connectionData)
    {
        var builder = new SqlConnectionStringBuilder(connectionData);
        var hosts = Hosts(builder);

        if (!string.IsNullOrEmpty(builder.AttachDBFilename))
        {
            return Unsafe("AttachDBFilename", "Remove AttachDBFilename and name the database with Initial Catalog", hosts);
        }

        if (builder.UserInstance)
        {
            return Unsafe("User Instance", "Remove User Instance, or set it to false", hosts);
        }

        if (Interactive.Contains(builder.Authentication))
        {
            return Unsafe("Authentication", "Use Authentication=SqlPassword, Active Directory Password or Active Directory Service Principal", hosts);
        }

        if (builder.IntegratedSecurity)
        {
            return HostCredentials("Integrated Security", "Remove Integrated Security (Trusted_Connection) and sign in with the login's User ID and Password", hosts);
        }

        if (!OwnCredentials.Contains(builder.Authentication))
        {
            return HostCredentials("Authentication", "Use Authentication=SqlPassword, Active Directory Password or Active Directory Service Principal", hosts);
        }

        if (builder.ColumnEncryptionSetting == SqlConnectionColumnEncryptionSetting.Enabled)
        {
            return HostCredentials("Column Encryption Setting", "Remove Column Encryption Setting, or set it to Disabled", hosts);
        }

        var servers = new[] { builder.DataSource, builder.FailoverPartner };
        if (servers.Any(x => LocalProtocols.Any(y => (x ?? string.Empty).TrimStart().StartsWith(y, StringComparison.OrdinalIgnoreCase))))
        {
            return HostCredentials("Data Source", "Connect over TCP to the server's host name (tcp:host,port), not a named pipe, shared memory or LocalDB", hosts);
        }

        return DataSourceConnectionOptionVerdict.Accept(hosts);
    }

    // "tcp:host\instance,port": the host of the server and of the failover partner; null when no server is named.
    private static List<string>? Hosts(SqlConnectionStringBuilder builder)
    {
        var hosts = new[] { builder.DataSource, builder.FailoverPartner }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Host(x!))
            .Where(x => x.Length > 0)
            .ToList();

        return string.IsNullOrWhiteSpace(builder.DataSource) || hosts.Count == 0 ? null : hosts;
    }

    private static string Host(string server)
    {
        var host = server.Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        host = host.Split(',', 2)[0];
        host = host.Split('\\', 2)[0];

        return host.Trim().Trim('[', ']');
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
