namespace Beacon.Core.Exceptions;

/// <summary>
/// A statement that passed the execution gate failed on the data source. Its message is the provider's: the server's
/// error for an ordinary source, generic text for a host data source. Not a business-rule violation, so the API does not
/// answer it with the message.
/// </summary>
public sealed class QueryExecutionFailedException(string message) : Exception(message)
{
    /// <summary>
    /// What the execution log records for the failure: the database's error redacted as with PII detection on, whatever the
    /// caller's setting (a host data source's generic text as it is). Null records <see cref="Exception.Message"/>.
    /// </summary>
    internal string? LogMessage { get; init; }
}
