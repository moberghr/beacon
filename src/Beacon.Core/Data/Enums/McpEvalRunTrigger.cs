namespace Beacon.Core.Data.Enums;

/// <summary>What started an <see cref="Entities.McpEvalRun"/>: a person, or the nightly scheduled eval.</summary>
public enum McpEvalRunTrigger
{
    Manual = 0,
    Scheduled = 1
}
