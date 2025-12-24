namespace Semantico.Core.Models;

public class SemanticoException : Exception
{
    public SemanticoException(string message) : base(message)
    {
    }

    public SemanticoException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
