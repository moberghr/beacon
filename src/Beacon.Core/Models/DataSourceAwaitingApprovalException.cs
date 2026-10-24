namespace Beacon.Core.Models;

/// <summary>
/// A connection refused because the data source's connection waits for a second person's approval, under the change
/// approval workflow's data-source connection switch: a created source until its first approval, a changed one until the
/// change is approved. Raised by the connection resolver, a held new source before anything is decrypted. The message is
/// generic: it names neither the source nor a value from its connection data. HTTP 400 with the message.
/// </summary>
public sealed class DataSourceAwaitingApprovalException(int dataSourceId, IReadOnlyList<int>? dataSourceIds = null)
    : BeaconException(RefusalMessage)
{
    /// <summary>What a caller is told: the same for every source waiting for approval.</summary>
    public const string RefusalMessage = "This data source's connection is waiting for approval: Beacon makes no connection with it until an Approver approves it.";

    /// <summary>The held source, for the caller's own records; never shown in the message.</summary>
    public int DataSourceId { get; } = dataSourceId;

    /// <summary>Every data source the refused work would have read (a run's), the held one included.</summary>
    public IReadOnlyList<int> DataSourceIds { get; } = dataSourceIds ?? [dataSourceId];
}
