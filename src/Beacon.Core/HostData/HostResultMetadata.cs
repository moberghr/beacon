namespace Beacon.Core.HostData;

/// <summary>Keys of the <c>ProviderQueryResult.Metadata</c> a result read from a host data source can carry.</summary>
public static class HostResultMetadata
{
    /// <summary>
    /// How many result columns were masked because the database named them differently than the host policy derived them
    /// (an <see cref="int"/>, present only when there were any): their values are hidden whatever the policy said of them,
    /// so a caller can say why a column it expected in the clear came back masked.
    /// </summary>
    public const string RenamedMaskedColumns = "HostColumnsMaskedForName";
}
