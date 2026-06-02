namespace Beacon.Core.Abstractions;

/// <summary>
/// Base contract for tracking execution history and metrics
/// </summary>
public interface IExecutionHistory
{
    /// <summary>
    /// Unique identifier for the execution
    /// </summary>
    int Id { get; }

    /// <summary>
    /// When the execution started
    /// </summary>
    DateTime StartedAt { get; }

    /// <summary>
    /// When the execution completed (null if still running)
    /// </summary>
    DateTime? CompletedAt { get; }

    /// <summary>
    /// Total execution duration
    /// </summary>
    TimeSpan ExecutionDuration { get; }

    /// <summary>
    /// Whether the execution was successful
    /// </summary>
    bool Success { get; }

    /// <summary>
    /// Error message if execution failed
    /// </summary>
    string? ErrorMessage { get; }
}
