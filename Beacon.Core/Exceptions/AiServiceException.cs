using Beacon.Core.Models;

namespace Beacon.Core.Exceptions;

public class AiServiceException : BeaconException
{
    public AiServiceException(string message) : base(message)
    {
    }

    public AiServiceException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
