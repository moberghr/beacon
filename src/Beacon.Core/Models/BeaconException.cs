namespace Beacon.Core.Models;

public class BeaconException : Exception
{
    public BeaconException(string message) : base(message)
    {
    }

    public BeaconException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
