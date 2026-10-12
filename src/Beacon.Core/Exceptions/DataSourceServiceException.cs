using Beacon.Core.Models;

namespace Beacon.Core.Exceptions;

/// <summary>
/// A data source service (Databricks, for one) refused or failed a statement and said why with an error code of its own
/// (<see cref="ErrorCode"/>, such as <c>INVALID_PARAMETER_VALUE</c>), which a log may name; the message is the service's
/// and may quote the statement or row values, so it never reaches a log (§1.11).
/// </summary>
public sealed class DataSourceServiceException(string message, string? errorCode) : BeaconException(message)
{
    public string? ErrorCode { get; } = errorCode;
}
