namespace Semantico.Core.Abstractions;

/// <summary>
/// Represents a job that can be scheduled for execution using cron expressions
/// </summary>
public interface IScheduledJob
{
    /// <summary>
    /// Unique identifier for the job
    /// </summary>
    int Id { get; }

    /// <summary>
    /// Human-readable name of the job
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Cron expression for scheduling (null = manual execution only)
    /// </summary>
    string? Schedule { get; }

    /// <summary>
    /// Whether the job is enabled for execution
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Maximum number of retry attempts on failure
    /// </summary>
    int MaxRetries { get; }

    /// <summary>
    /// Timeout in minutes for job execution
    /// </summary>
    int TimeoutMinutes { get; }
}
