using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// Which data-source connections Beacon accepts beyond their transport, bound from <c>Beacon:DataSources</c>. Each
/// connector's <see cref="Services.Providers.IDataSourceConnectionOptionInspector"/> reads the connection data: a new or
/// changed connection that sets a driver option Beacon does not accept, or reaches a host <see cref="AllowedHosts"/> does
/// not list, is refused; a stored one keeps working with one Warning per data source per process, unless
/// <c>Beacon:DataSources:Transport:EnforceOnExisting</c> is set. <c>Beacon:DataSources:Transport:AllowInsecure</c> lifts
/// the option check (development hosts), never the host list.
/// </summary>
public sealed class DataSourceConnectionOptions
{
    public const string SectionName = "Beacon:DataSources";

    /// <summary>
    /// The hosts a data-source connection may reach: exact host names or IP addresses, or <c>*.example.com</c> for any
    /// host below <c>example.com</c> (not <c>example.com</c> itself); case-insensitive, ASCII only (an internationalised
    /// name in its <c>xn--</c> form), IP addresses compared in their canonical form. Empty (the default) leaves hosts
    /// unrestricted. When set, a connection whose hosts its connector cannot tell, or a host that is not a bare host name
    /// or IP address, is refused. The names are matched as the connection writes them: the addresses they resolve to are
    /// not pinned, so the list trusts the DNS the Beacon host uses.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// Accepts connections that sign in as the Beacon host (integrated or Kerberos security, a managed, workload or
    /// default cloud identity, a cloud profile, the host's certificate store, a local socket or named pipe, a credentials
    /// file). Default false: an Admin's connection must carry its own credentials.
    /// </summary>
    public bool AllowHostCredentials { get; set; }
}

/// <summary>Refuses at startup an <see cref="DataSourceConnectionOptions.AllowedHosts"/> entry that is not a host name.</summary>
internal sealed class DataSourceConnectionOptionsValidator : IValidateOptions<DataSourceConnectionOptions>
{
    public ValidateOptionsResult Validate(string? name, DataSourceConnectionOptions options)
    {
        var invalid = options.AllowedHosts
            .Select((x, i) => (Entry: x, Index: i))
            .Where(x => !IsHostPattern(x.Entry))
            .Select(x => x.Index)
            .ToList();

        return invalid.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{DataSourceConnectionOptions.SectionName}:{nameof(DataSourceConnectionOptions.AllowedHosts)} entries {string.Join(", ", invalid)} are not host names: use a host name, an IP address or *.example.com, without a scheme, port or path.");
    }

    private static bool IsHostPattern(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var host = entry.Trim();

        return host.StartsWith("*.", StringComparison.Ordinal)
            ? DataSourceHostName.Name(host[2..]) != null
            : DataSourceHostName.Canonical(host) != null;
    }
}
