using Cronos;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services;

/// <summary>
/// The settings of a migration job that Beacon acts on, and the values it accepts for those it does not: a job is created
/// only with a mode that is one of the three, a timeout of 1 to 1,440 minutes (each run is stopped after it), validation
/// before execution on (every run checks its source SQL and destination table first; it cannot be turned off), max
/// retries 0 or the default 3 (a failed run is never retried) and a schedule that is empty or a valid cron expression
/// (stored for a host's own scheduler; Beacon does not start runs from it).
/// </summary>
internal static class MigrationJobSettings
{
    public const int MinTimeoutMinutes = 1;

    public const int MaxTimeoutMinutes = 1440;

    /// <summary>The max retries a job is created with when none is given; like 0, it retries nothing.</summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>
    /// How long past its timeout a run row still counts as a run in progress: a row Running for longer was left by a host
    /// that stopped during the run, and no longer blocks the job.
    /// </summary>
    public const int AbandonedRunMarginMinutes = 10;

    private const string ModeRefusal = "Migration mode must be Insert (1), Upsert (2) or Truncate (3).";

    /// <summary>Why a job with these settings is not created; null when every setting is one Beacon acts on.</summary>
    public static string? Refusal(MigrationMode mode, string? schedule, int maxRetries, int timeoutMinutes, bool validateBeforeExecution)
    {
        if (!Enum.IsDefined(mode))
        {
            return ModeRefusal;
        }

        if (timeoutMinutes is < MinTimeoutMinutes or > MaxTimeoutMinutes)
        {
            return $"Timeout must be between {MinTimeoutMinutes} and {MaxTimeoutMinutes} minutes.";
        }

        if (maxRetries != 0 && maxRetries != DefaultMaxRetries)
        {
            return $"Automatic retries are not supported: a failed run is not retried. Leave max retries at {DefaultMaxRetries} or set it to 0.";
        }

        if (!validateBeforeExecution)
        {
            return "Validation before execution cannot be turned off: every run checks its source query and destination table before it writes.";
        }

        if (!string.IsNullOrWhiteSpace(schedule) && !IsCronExpression(schedule))
        {
            return "The schedule must be empty or a standard 5-field cron expression.";
        }

        return null;
    }

    /// <summary>Why a run of a job with this mode does not start; null for one of the three modes.</summary>
    public static string? RunRefusal(MigrationMode mode) => Enum.IsDefined(mode) ? null : ModeRefusal;

    /// <summary>How long a run of a job with this stored timeout may last: the timeout, clamped to 1–1,440 minutes.</summary>
    public static TimeSpan RunTimeout(int timeoutMinutes) =>
        TimeSpan.FromMinutes(Math.Clamp(timeoutMinutes, MinTimeoutMinutes, MaxTimeoutMinutes));

    /// <summary>How long a run row of a job with this stored timeout counts as a run in progress.</summary>
    public static TimeSpan RunInProgressWindow(int timeoutMinutes) =>
        RunTimeout(timeoutMinutes) + TimeSpan.FromMinutes(AbandonedRunMarginMinutes);

    private static bool IsCronExpression(string schedule)
    {
        try
        {
            CronExpression.Parse(schedule);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }
}
