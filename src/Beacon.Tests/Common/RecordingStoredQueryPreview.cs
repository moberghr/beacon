using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;

namespace Beacon.Tests.Common;

/// <summary>
/// The stored-query preview seam for preview-service tests: loads a snapshot without steps, answers every run with
/// <see cref="Answer"/> (which may throw, as a failed run does) and records what was loaded and run, and the given steps it
/// was asked to run in-process (answered with <see cref="TemporaryAnswer"/>).
/// </summary>
internal sealed class RecordingStoredQueryPreview : IStoredQueryPreview
{
    public Func<QueryPreviewResult> Answer { get; init; } = () => new QueryPreviewResult { Success = true };

    public Func<QueryExecutionResult> TemporaryAnswer { get; init; } = () => new QueryExecutionResult { Success = true };

    public List<int> Loads { get; } = [];

    /// <summary>Each run: the step it ran on its own (null for the whole query) and the page it asked for.</summary>
    public List<(int? StepOrder, ListRequest Paging)> Runs { get; } = [];

    /// <summary>The given steps it ran in-process, with the row limit of each run.</summary>
    public List<(QueryData Query, int? MaxRows)> TemporaryRuns { get; } = [];

    public Task<QueryExecutionResult> ExecuteTemporaryQuery(
        QueryData queryData,
        List<ParameterValue>? parameters,
        int? maxRows,
        CancellationToken cancellationToken)
    {
        TemporaryRuns.Add((queryData, maxRows));

        return Task.FromResult(TemporaryAnswer());
    }

    public Task<StoredQuerySnapshot> LoadSnapshot(int queryId, CancellationToken cancellationToken)
    {
        Loads.Add(queryId);

        return Task.FromResult(new StoredQuerySnapshot { QueryId = queryId, Steps = [] });
    }

    public Task<QueryPreviewResult> PreviewSnapshot(StoredQuerySnapshot snapshot, ListRequest paging, CancellationToken cancellationToken)
    {
        Runs.Add((null, paging));

        return Task.FromResult(Answer());
    }

    public Task<QueryPreviewResult> PreviewSnapshotStep(
        StoredQuerySnapshot snapshot,
        int stepOrder,
        List<ParameterValue>? parameters,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        Runs.Add((stepOrder, paging));

        return Task.FromResult(Answer());
    }
}
