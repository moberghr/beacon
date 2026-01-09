using Semantico.Core.Models;

namespace Semantico.Core.Exceptions;

public class AiServiceException : SemanticoException
{
    public AiServiceException(string message) : base(message)
    {
    }

    public AiServiceException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
