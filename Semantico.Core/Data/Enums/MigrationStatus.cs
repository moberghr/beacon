namespace Semantico.Core.Data.Enums;

public enum MigrationStatus
{
    Queued = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    PartialSuccess = 6  // Some rows failed but execution completed
}