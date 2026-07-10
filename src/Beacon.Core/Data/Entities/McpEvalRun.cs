using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// A batch execution of the MCP eval harness over the active golden cases. Records aggregate
/// execution-accuracy plus per-run configuration. Individual case outcomes live in
/// <see cref="McpEvalResult"/> (linked by <c>EvalRunId</c>).
/// </summary>
public class McpEvalRun : BaseEntity
{
    public int? ProjectId { get; set; }
    public int? TriggeredByUserId { get; set; }

    public int TotalCases { get; set; }
    public int PassedCases { get; set; }
    public double ExecutionAccuracy { get; set; }

    /// <summary>"Running", "Completed", or "Failed".</summary>
    public string Status { get; set; } = "Running";

    public bool JudgeEnabled { get; set; }
    public string? Notes { get; set; }
}
