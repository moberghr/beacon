using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// Reads a data source's connection data with its driver's own parser (option synonyms and case included) and says
/// whether the connection authenticates the server over TLS. One per connector, registered by its
/// <c>Add{Engine}Connector()</c>; <see cref="IDataSourceTransportPolicy"/> decides what a failing verdict means. An
/// inspector never connects and never returns a value read from the connection data in its remedy.
/// </summary>
public interface IDataSourceTransportInspector
{
    DataSourceType DataSourceType { get; }

    /// <summary>The engine of a <see cref="DataSourceType.Database"/> source; null for every other type.</summary>
    DatabaseEngineType? DatabaseEngineType { get; }

    DataSourceTransportVerdict Inspect(string connectionData);
}

/// <summary>
/// Whether a connection authenticates the server over TLS. When it does not, <see cref="Option"/> names the option to
/// change and <see cref="Remedy"/> says what to set it to; both are fixed text of the inspector, never a value read from
/// the connection data. <see cref="Target"/> is where the connection goes, when the inspector can tell, and
/// <see cref="Definition"/> the whole connection as the driver's builder normalizes it, without its secrets, when the
/// inspector gives one.
/// </summary>
public sealed record DataSourceTransportVerdict
{
    public bool Verified { get; private init; }

    public string? Option { get; private init; }

    public string? Remedy { get; private init; }

    public DataSourceTransportTarget? Target { get; private init; }

    /// <summary>The connection data could not be read at all (empty, or rejected by the connector's parser).</summary>
    public bool Unreadable { get; private init; }

    /// <summary>
    /// The connection's definition (canonical setting names, settings left at their default left out, secrets removed),
    /// whatever the verdict; null when the inspector gives none or the connection data could not be read. It holds
    /// identifiers and is never logged.
    /// </summary>
    public DataSourceConnectionDefinition? Definition { get; private init; }

    public static DataSourceTransportVerdict Pass(DataSourceTransportTarget? target = null)
    {
        return new DataSourceTransportVerdict { Verified = true, Target = target };
    }

    public static DataSourceTransportVerdict Fail(string option, string remedy, DataSourceTransportTarget? target = null)
    {
        return new DataSourceTransportVerdict { Verified = false, Option = option, Remedy = remedy, Target = target };
    }

    /// <summary>The connection data could not be parsed: nothing can be verified. The parser's message is not kept.</summary>
    public static DataSourceTransportVerdict NotReadable()
    {
        return Fail("connection string", "Enter a connection string the engine's driver can read") with { Unreadable = true };
    }

    /// <summary>This verdict with the connection's definition.</summary>
    public DataSourceTransportVerdict WithDefinition(DataSourceConnectionDefinition? definition)
    {
        return this with { Definition = definition };
    }

    /// <summary>The verdict and the inspector's fixed text only: the target and the definition are never printed.</summary>
    public override string ToString()
    {
        return $"{nameof(DataSourceTransportVerdict)} {{ Verified = {Verified}, Option = {Option}, Unreadable = {Unreadable} }}";
    }
}

/// <summary>
/// Where a connection goes: host, port and database (catalog, project), as identifiers. Never a credential; still not
/// logged (§1.11).
/// </summary>
public sealed record DataSourceTransportTarget(string? Host, int? Port, string? Database);
