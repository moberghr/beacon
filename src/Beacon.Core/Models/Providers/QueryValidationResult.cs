namespace Beacon.Core.Models.Providers;

public class QueryValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Suggested fixes or improvements
    /// </summary>
    public List<string> Suggestions { get; set; } = new();

    /// <summary>
    /// True when the provider performed NO validation at all (e.g. the database engine has no dry-run
    /// strategy). Distinct from a failed validation: <see cref="IsValid"/> is false because nothing was
    /// checked — not because the query is known-bad. Callers must treat a skipped result as
    /// "nothing verified" (surface it honestly, never repair against it).
    /// </summary>
    public bool Skipped { get; set; }
}
