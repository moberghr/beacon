using Npgsql;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.PostgreSql;

/// <summary>
/// The Npgsql options Beacon does not accept from an Admin, read with Npgsql's own builder: <c>Options</c> (server
/// settings sent when the connection opens), <c>Passfile</c> (a file on the Beacon host), <c>Include Error Detail</c> and
/// <c>Log Parameters</c> (row values in errors and logs), and signing in as the host: a Unix-socket <c>Host</c> (peer
/// authentication), <c>GSS Encryption Mode=Require</c> (Kerberos), or no <c>Password</c> and no client certificate (the
/// host's <c>PGPASSWORD</c> or pgpass file). The hosts are every host <c>Host</c> lists.
/// </summary>
public sealed class PostgreSqlConnectionOptionInspector : IDataSourceConnectionOptionInspector
{
    public DataSourceType DataSourceType => DataSourceType.Database;

    public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.PostgreSQL;

    public DataSourceConnectionOptionVerdict Inspect(string connectionData)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionData);
        var hosts = Hosts(builder.Host);

        if (!string.IsNullOrEmpty(builder.Options))
        {
            return Unsafe("Options", "Remove Options; set what the server needs on the login role instead", hosts);
        }

        if (!string.IsNullOrEmpty(builder.Passfile))
        {
            return Unsafe("Passfile", "Remove Passfile and give the password in the connection", hosts);
        }

        if (builder.IncludeErrorDetail)
        {
            return Unsafe("Include Error Detail", "Remove Include Error Detail, or set it to false", hosts);
        }

        if (builder.LogParameters)
        {
            return Unsafe("Log Parameters", "Remove Log Parameters, or set it to false", hosts);
        }

        if (hosts != null && hosts.Any(x => x.StartsWith('/') || x.StartsWith('@')))
        {
            return HostCredentials("Host", "Connect over TCP to the server's host name, not a Unix socket", hosts);
        }

        if (builder.GssEncryptionMode == GssEncryptionMode.Require)
        {
            return HostCredentials("GSS Encryption Mode", "Remove GSS Encryption Mode, or set it to Disable or Prefer, and sign in with a password", hosts);
        }

        if (string.IsNullOrEmpty(builder.Password) && string.IsNullOrEmpty(builder.SslCertificate))
        {
            return HostCredentials("Password", "Give the login's Password (or a client certificate with SSL Certificate)", hosts);
        }

        return DataSourceConnectionOptionVerdict.Accept(hosts);
    }

    // "host1:5432,host2": each host without its port (an IPv6 address is written in brackets); null when none is named.
    private static List<string>? Hosts(string? host)
    {
        var hosts = (host ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(WithoutPort)
            .ToList();

        return hosts.Count == 0 ? null : hosts;
    }

    private static string WithoutPort(string host)
    {
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            return end > 0 ? host[1..end] : host;
        }

        if (host.StartsWith('/') || host.StartsWith('@'))
        {
            return host;
        }

        var colon = host.LastIndexOf(':');
        return colon > 0 && host.IndexOf(':') == colon ? host[..colon] : host;
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
