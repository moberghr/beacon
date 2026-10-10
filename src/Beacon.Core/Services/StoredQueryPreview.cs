using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

/// <summary>
/// A stored query's steps and final query, read once and detached from any context. A preview authorizes the data
/// sources of exactly these steps and then runs exactly them, so an edit saved in between cannot run a step whose
/// source was never authorized for the caller.
/// </summary>
internal sealed class StoredQuerySnapshot
{
    public required int QueryId { get; init; }

    /// <summary>The steps in <see cref="QueryStep.StepOrder"/> order, each with its data source and parameters.</summary>
    public required IReadOnlyList<QueryStep> Steps { get; init; }

    public string? FinalQuery { get; init; }

    /// <summary>The distinct data sources the steps read; empty for a query without steps.</summary>
    public IReadOnlyList<int> DataSourceIds => Steps
        .Select(x => x.DataSourceId)
        .Distinct()
        .Order()
        .ToList();
}

/// <summary>
/// Previews of a stored query that run a <see cref="StoredQuerySnapshot"/> the caller loaded and authorized: the seam
/// <see cref="QueryExecutionPreviewService"/> re-checks the sources through right before the SQL runs.
/// </summary>
internal interface IStoredQueryPreview
{
    Task<StoredQuerySnapshot> LoadSnapshot(int queryId, CancellationToken cancellationToken);

    /// <summary>One page of the snapshot's result (steps summarised, final or single result paged).</summary>
    Task<QueryPreviewResult> PreviewSnapshot(StoredQuerySnapshot snapshot, ListRequest paging, CancellationToken cancellationToken);

    /// <summary>One page of the snapshot's step <paramref name="stepOrder"/>, run on its own.</summary>
    Task<QueryPreviewResult> PreviewSnapshotStep(
        StoredQuerySnapshot snapshot,
        int stepOrder,
        List<ParameterValue>? parameters,
        ListRequest paging,
        CancellationToken cancellationToken);
}
