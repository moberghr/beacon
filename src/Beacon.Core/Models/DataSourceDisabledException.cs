namespace Beacon.Core.Models;

/// <summary>
/// A connection refused because a DataSourceAdmin disabled the data source. Raised by the connection resolver before
/// anything is decrypted, so nothing connects with a disabled source's credentials, and by a subscription run before any
/// step runs. The message is generic: it names neither the source nor a value from its connection data. HTTP 400 with the
/// message.
/// </summary>
public sealed class DataSourceDisabledException(int dataSourceId, IReadOnlyList<int>? dataSourceIds = null)
    : BeaconException(RefusalMessage)
{
    /// <summary>What a caller is told: the same for every disabled source.</summary>
    public const string RefusalMessage = "This data source is disabled: Beacon makes no connection with it until a DataSourceAdmin enables it again.";

    /// <summary>The disabled source, for the caller's own records; never shown in the message.</summary>
    public int DataSourceId { get; } = dataSourceId;

    /// <summary>Every data source the refused work would have read (a run's), the disabled one included.</summary>
    public IReadOnlyList<int> DataSourceIds { get; } = dataSourceIds ?? [dataSourceId];
}
