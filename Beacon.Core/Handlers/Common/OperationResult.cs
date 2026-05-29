namespace Beacon.Core.Handlers.Common;

/// <summary>
/// Generic success/failure response carrier for command handlers that don't
/// otherwise have a domain-specific result type. Prefer this over inventing
/// another <c>record XxxResult(bool Success, string? ErrorMessage)</c>.
/// </summary>
public record OperationResult(bool Success, string? ErrorMessage = null)
{
    public static OperationResult Ok() => new(true);
    public static OperationResult Fail(string message) => new(false, message);
}
