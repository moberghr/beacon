namespace Beacon.Core.Data.Enums;

public enum McpEvalFailureTag
{
    None = 0,
    RetrievalFailure = 1,
    SqlReasoningFailure = 2,
    ExecutionError = 3,

    /// <summary>
    /// The case could NOT be evaluated because the harness itself failed (LLM generation threw, a
    /// dependency/DB errored, the data source was missing) — distinct from a genuine text-to-SQL miss.
    /// These cases are excluded from the execution-accuracy denominator so an outage during a run does
    /// not silently deflate the headline metric.
    /// </summary>
    HarnessError = 4
}
