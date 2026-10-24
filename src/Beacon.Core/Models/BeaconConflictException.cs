namespace Beacon.Core.Models;

/// <summary>
/// A valid request that conflicts with how the deployment is set up (for example project membership the host manages):
/// HTTP 409 with the message.
/// </summary>
public sealed class BeaconConflictException : BeaconException
{
    public BeaconConflictException(string message) : base(message)
    {
    }

    public BeaconConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
