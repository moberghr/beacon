namespace Beacon.Core.Models;

/// <summary>
/// The resource is missing, or the caller may not read it: both answer HTTP 404 without a message, so a caller cannot
/// tell a resource it may not see from one that does not exist.
/// </summary>
public sealed class BeaconNotFoundException(string message) : BeaconException(message);
