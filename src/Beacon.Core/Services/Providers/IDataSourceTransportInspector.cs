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
/// the connection data. <see cref="Target"/> is where the connection goes, when the inspector can tell.
/// </summary>
public sealed record DataSourceTransportVerdict
{
    public bool Verified { get; private init; }

    public string? Option { get; private init; }

    public string? Remedy { get; private init; }

    public DataSourceTransportTarget? Target { get; private init; }

    public static DataSourceTransportVerdict Pass(DataSourceTransportTarget? target = null)
    {
        return new DataSourceTransportVerdict { Verified = true, Target = target };
    }

    public static DataSourceTransportVerdict Fail(string option, string remedy, DataSourceTransportTarget? target = null)
    {
        return new DataSourceTransportVerdict { Verified = false, Option = option, Remedy = remedy, Target = target };
    }

    /// <summary>The connection data could not be parsed: nothing can be verified. The parser's message is not kept.</summary>
    public static DataSourceTransportVerdict Unreadable()
    {
        return Fail("connection string", "Enter a connection string the engine's driver can read");
    }
}

/// <summary>
/// Where a connection goes: host, port and database (catalog, project), as identifiers. Never a credential; still not
/// logged (§1.11).
/// </summary>
public sealed record DataSourceTransportTarget(string? Host, int? Port, string? Database);
