namespace Beacon.Core.Data.Enums;

/// <summary>Where a statement in the execution log is.</summary>
public enum QueryExecutionLogStatus
{
    /// <summary>Recorded before the statement was sent; its end is not recorded yet.</summary>
    Started = 1,
    Succeeded = 2,
    Failed = 3,
    /// <summary>The caller cancelled it while it ran.</summary>
    Cancelled = 4,
    /// <summary>It ran past its statement timeout.</summary>
    TimedOut = 5,
    /// <summary>
    /// Never stored: how the execution log's read and export show a Started row older than the statement timeout plus a
    /// margin, a statement whose end was never recorded (the process stopped, the log store failed).
    /// </summary>
    Abandoned = 6
}
