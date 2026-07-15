using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// The outcome of running one <see cref="McpEvalCase"/> within an <see cref="McpEvalRun"/>:
/// the generated SQL, execution-accuracy pass/fail, a failure tag (retrieval vs SQL-reasoning vs
/// execution), and optional LLM-as-judge verdict. Plain int FKs (<c>EvalRunId</c>, <c>EvalCaseId</c>)
/// per the Mcp learning entity convention — no navigation properties.
/// </summary>
public class McpEvalResult : BaseEntity
{
    public int EvalRunId { get; set; }
    public int EvalCaseId { get; set; }

    public string? GeneratedSql { get; set; }
    public bool Passed { get; set; }
    public McpEvalFailureTag FailureTag { get; set; }
    public string? ExecutionError { get; set; }

    public bool JudgeUsed { get; set; }
    public string? JudgeVerdict { get; set; }

    public int? ResultRowCount { get; set; }
    public int ExecutionTimeMs { get; set; }
}
