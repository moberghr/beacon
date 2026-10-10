namespace Beacon.Core.Models;

/// <summary>The caller sent too many requests of this kind in a short time: HTTP 429.</summary>
public sealed class BeaconRateLimitedException(string message) : BeaconException(message);
