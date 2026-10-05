namespace Beacon.Core.Models.Queries;

/// <summary>
/// The outcome of a query or step preview: a summary of each step plus ONE page of the result. A preview
/// never returns every row — see <see cref="QueryResultPage"/>.
/// </summary>
public sealed class QueryPreviewResult
{
    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public double TotalExecutionTimeMs { get; init; }

    public List<string> DataSourcesInvolved { get; init; } = [];

    public List<QueryPreviewStep> Steps { get; init; } = [];

    /// <summary>
    /// The paged result: the step itself for a single-step query (or a step preview), the final query of a
    /// multi-step one. Null when a step failed, or a multi-step query has no final query.
    /// </summary>
    public QueryResultPage? Result { get; init; }
}

public sealed class QueryPreviewStep
{
    public int StepOrder { get; init; }

    public string StepName { get; init; } = string.Empty;

    public string DataSourceName { get; init; } = string.Empty;

    public string DatabaseEngine { get; init; } = string.Empty;

    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public double ExecutionTimeMs { get; init; }

    public int TotalRows { get; init; }

    /// <summary>
    /// The first rows of an intermediate step. The step feeds the final query in full, but only these
    /// travel to the client.
    /// </summary>
    public List<IDictionary<string, object?>> PreviewRows { get; init; } = [];
}

/// <summary>One page of a raw result, ordered by <see cref="Sort"/> when the SQL shape allowed it.</summary>
public sealed class QueryResultPage
{
    public List<IDictionary<string, object?>> Rows { get; init; } = [];

    public int TotalCount { get; init; }

    public int PageCount { get; init; }

    public int Page { get; init; }

    public int PageSize { get; init; }

    /// <summary>False when this result cannot be sorted by column (the grid disables its sortable headers).</summary>
    public bool Sortable { get; init; }

    /// <summary>The sort that was applied (<c>-column</c> syntax), or null for the query's own order.</summary>
    public string? Sort { get; init; }
}
