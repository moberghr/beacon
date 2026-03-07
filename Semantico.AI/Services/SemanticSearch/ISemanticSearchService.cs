namespace Semantico.AI.Services.SemanticSearch;

public interface ISemanticSearchService
{
    Task<SemanticSearchResult> AskAsync(int dataSourceId, string question, bool execute = true, CancellationToken ct = default);
}

public record SemanticSearchResult(string GeneratedSql, string? Explanation, List<Dictionary<string, object?>>? Results, string? Error);
