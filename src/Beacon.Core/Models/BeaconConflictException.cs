namespace Beacon.Core.Models;

/// <summary>
/// A valid request that conflicts with how the deployment is set up (for example project membership the host manages):
/// HTTP 409 with the message.
/// </summary>
public sealed class BeaconConflictException(string message) : BeaconException(message);
