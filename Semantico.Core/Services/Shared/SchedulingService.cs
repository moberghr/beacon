using Cronos;
using Microsoft.Extensions.Logging;
using Semantico.Core.Abstractions;

namespace Semantico.Core.Services.Shared;

/// <summary>
/// Generic scheduling service for any IScheduledJob
/// Provides common scheduling, retry, and timeout logic
/// </summary>
internal class SchedulingService
{
    private readonly ILogger<SchedulingService> _logger;

    public SchedulingService(ILogger<SchedulingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Determines if a failed execution should be retried
    /// </summary>
    public bool ShouldRetry(IExecutionHistory execution, IScheduledJob job, int currentAttempt)
    {
        if (execution.Success)
        {
            _logger.LogDebug("Execution {ExecutionId} succeeded, no retry needed", execution.Id);
            return false;
        }

        if (currentAttempt >= job.MaxRetries)
        {
            _logger.LogWarning("Execution {ExecutionId} failed after {Attempts} attempts (max: {MaxRetries})",
                execution.Id, currentAttempt, job.MaxRetries);
            return false;
        }

        _logger.LogInformation("Execution {ExecutionId} failed, will retry (attempt {Attempt}/{MaxRetries})",
            execution.Id, currentAttempt + 1, job.MaxRetries);
        return true;
    }

    /// <summary>
    /// Validates if a cron expression is valid
    /// </summary>
    public bool IsValidCronExpression(string? cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
            return false;

        return CronExpression.TryParse(cronExpression, out _);
    }

    /// <summary>
    /// Calculates the next execution time based on cron schedule
    /// </summary>
    public DateTime? GetNextExecutionTime(string? cronExpression, DateTime fromTime)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
            return null;

        if (!CronExpression.TryParse(cronExpression, out var expression))
            return null;

        return expression.GetNextOccurrence(fromTime, inclusive: true);
    }

    /// <summary>
    /// Checks if a job execution has exceeded its timeout
    /// </summary>
    public bool HasExceededTimeout(IExecutionHistory execution, IScheduledJob job)
    {
        if (execution.CompletedAt.HasValue)
            return false; // Already completed

        var runningTime = DateTime.UtcNow - execution.StartedAt;
        var timeout = TimeSpan.FromMinutes(job.TimeoutMinutes);

        if (runningTime > timeout)
        {
            _logger.LogWarning("Execution {ExecutionId} exceeded timeout of {Timeout} minutes",
                execution.Id, job.TimeoutMinutes);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates that a job is enabled and ready for execution
    /// </summary>
    public ValidationResult ValidateJobForExecution(IScheduledJob job)
    {
        if (!job.IsEnabled)
        {
            return ValidationResult.Failure($"Job '{job.Name}' is disabled and cannot be executed");
        }

        if (job.TimeoutMinutes <= 0)
        {
            return ValidationResult.Failure($"Job '{job.Name}' has invalid timeout: {job.TimeoutMinutes}");
        }

        if (job.MaxRetries < 0)
        {
            return ValidationResult.Failure($"Job '{job.Name}' has invalid max retries: {job.MaxRetries}");
        }

        return ValidationResult.Success();
    }
}
