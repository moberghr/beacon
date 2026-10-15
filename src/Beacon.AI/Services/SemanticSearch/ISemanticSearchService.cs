namespace Beacon.AI.Services.SemanticSearch;

public interface ISemanticSearchService
{
    /// <summary>
    /// Answers <paramref name="question"/> with SQL generated against one data source, through the caller's
    /// <paramref name="projectId"/>: the caller needs the Query action there (a project it may not read answers
    /// <see cref="Beacon.Core.Models.BeaconNotFoundException"/>, one where it may not query
    /// <see cref="Beacon.Core.Models.BeaconForbiddenException"/>), and the grounding comes from that project only, so a
    /// data source the project does not link is reported as not found.
    /// </summary>
    Task<SemanticSearchResult> AskAsync(int dataSourceId, int projectId, string question, bool execute = true, CancellationToken ct = default);
}

public record SemanticSearchResult(string GeneratedSql, string? Explanation, List<Dictionary<string, object?>>? Results, string? Error);
