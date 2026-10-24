using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// Reads a data source's connection data with its driver's own parser and says whether it sets a driver option Beacon does
/// not accept from an Admin (one that reads a file on the Beacon host, runs code or SQL when the connection opens, signs
/// in interactively on the server, or signs in as the Beacon host itself), and which hosts the connection reaches. One per
/// connector, registered by its <c>Add{Engine}Connector()</c> next to its <see cref="IDataSourceTransportInspector"/>;
/// <see cref="IDataSourceTransportPolicy"/> decides what a refusal means. An inspector never connects and never returns a
/// value read from the connection data in its remedy.
/// </summary>
public interface IDataSourceConnectionOptionInspector
{
    DataSourceType DataSourceType { get; }

    /// <summary>The engine of a <see cref="DataSourceType.Database"/> source; null for every other type.</summary>
    DatabaseEngineType? DatabaseEngineType { get; }

    DataSourceConnectionOptionVerdict Inspect(string connectionData);
}

/// <summary>Why a connection option is refused.</summary>
public enum DataSourceConnectionOptionRisk
{
    /// <summary>The option reads a file on the Beacon host, runs code or SQL on connect, or signs in interactively.</summary>
    Unsafe,

    /// <summary>
    /// The connection signs in as the Beacon host: its OS account or Windows/Kerberos identity, a managed or workload
    /// identity, a cloud role, profile or default credentials, a certificate store or a credentials file on the host.
    /// Lifted by <c>Beacon:DataSources:AllowHostCredentials</c>.
    /// </summary>
    HostCredentials
}

/// <summary>
/// Whether a connection's driver options are accepted. A refusal names the option (<see cref="Option"/>) and what to do
/// (<see cref="Remedy"/>), both fixed text of the inspector, never a value read from the connection data.
/// <see cref="Hosts"/> are the hosts the connection data names (an empty list: the connector reaches only its vendor's
/// fixed endpoints; null: the inspector cannot tell), checked against <c>Beacon:DataSources:AllowedHosts</c>. They are
/// identifiers, never logged.
/// </summary>
public sealed record DataSourceConnectionOptionVerdict
{
    public bool Accepted { get; private init; }

    public string? Option { get; private init; }

    public string? Remedy { get; private init; }

    public DataSourceConnectionOptionRisk? Risk { get; private init; }

    public IReadOnlyList<string>? Hosts { get; private init; }

    /// <summary>The connection data could not be read at all (rejected by the connector's parser).</summary>
    public bool Unreadable { get; private init; }

    public static DataSourceConnectionOptionVerdict Accept(IReadOnlyList<string>? hosts)
    {
        return new DataSourceConnectionOptionVerdict { Accepted = true, Hosts = hosts };
    }

    /// <summary>
    /// A refused option; <paramref name="hosts"/> are still checked against the host list when a host lifts the refusal.
    /// </summary>
    public static DataSourceConnectionOptionVerdict Refuse(
        string option,
        string remedy,
        DataSourceConnectionOptionRisk risk,
        IReadOnlyList<string>? hosts = null)
    {
        return new DataSourceConnectionOptionVerdict { Accepted = false, Option = option, Remedy = remedy, Risk = risk, Hosts = hosts };
    }

    /// <summary>The connection data could not be parsed: no option can be checked. The parser's message is not kept.</summary>
    public static DataSourceConnectionOptionVerdict NotReadable()
    {
        return Refuse("connection string", "Enter a connection string the engine's driver can read", DataSourceConnectionOptionRisk.Unsafe) with { Unreadable = true };
    }

    /// <summary>The verdict and the inspector's fixed text only: the hosts are never printed.</summary>
    public override string ToString()
    {
        return $"{nameof(DataSourceConnectionOptionVerdict)} {{ Accepted = {Accepted}, Option = {Option}, Risk = {Risk}, Unreadable = {Unreadable} }}";
    }
}
