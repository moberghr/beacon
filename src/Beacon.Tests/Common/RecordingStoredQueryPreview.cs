using Beacon.Core.Data.Entities;
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

    /// <summary>The answer to a run of given steps (which may throw or wait, as a failed or slow run does); settable mid-test.</summary>
    public Func<CancellationToken, Task<QueryExecutionResult>> TemporaryAnswer { get; set; } = _ => Task.FromResult(new QueryExecutionResult { Success = true });

    /// <summary>The steps of every snapshot it loads (none by default).</summary>
    public IReadOnlyList<QueryStep> SnapshotSteps { get; init; } = [];

    public List<int> Loads { get; } = [];

    /// <summary>Each run: the step it ran on its own (null for the whole query) and the page it asked for.</summary>
    public List<(int? StepOrder, ListRequest Paging)> Runs { get; } = [];

    /// <summary>Each draft run: the step it ran on its own (null for the whole draft) and the draft.</summary>
    public List<(int? StepOrder, QueryDraft Draft)> DraftRuns { get; } = [];

    /// <summary>Whether each run (stored or draft, in order) was asked to PII-mask its rows.</summary>
    public List<bool> Masking { get; } = [];

    /// <summary>The given steps it ran in-process, with the row limit of each run.</summary>
    public List<(QueryData Query, int? MaxRows)> TemporaryRuns { get; } = [];

    public async Task<QueryExecutionResult> ExecuteTemporaryQuery(
        QueryData queryData,
        List<ParameterValue>? parameters,
        int? maxRows,
        CancellationToken cancellationToken)
    {
        TemporaryRuns.Add((queryData, maxRows));

        return await TemporaryAnswer(cancellationToken);
    }

    public Task<StoredQuerySnapshot> LoadSnapshot(int queryId, CancellationToken cancellationToken)
    {
        Loads.Add(queryId);

        return Task.FromResult(new StoredQuerySnapshot { QueryId = queryId, Steps = SnapshotSteps });
    }

    public Task<QueryPreviewResult> PreviewSnapshot(StoredQuerySnapshot snapshot, ListRequest paging, bool maskPii, CancellationToken cancellationToken)
    {
        Runs.Add((null, paging));
        Masking.Add(maskPii);

        return Task.FromResult(Answer());
    }

    public Task<QueryPreviewResult> PreviewSnapshotStep(
        StoredQuerySnapshot snapshot,
        int stepOrder,
        List<ParameterValue>? parameters,
        ListRequest paging,
        bool maskPii,
        CancellationToken cancellationToken)
    {
        Runs.Add((stepOrder, paging));
        Masking.Add(maskPii);

        return Task.FromResult(Answer());
    }

    public Task<QueryPreviewResult> PreviewDraft(int queryId, QueryDraft draft, ListRequest paging, bool maskPii, CancellationToken cancellationToken)
    {
        DraftRuns.Add((null, draft));
        Masking.Add(maskPii);

        return Task.FromResult(Answer());
    }

    public Task<QueryPreviewResult> PreviewDraftStep(
        int queryId,
        int stepOrder,
        List<ParameterValue>? parameters,
        QueryDraft draft,
        ListRequest paging,
        bool maskPii,
        CancellationToken cancellationToken)
    {
        DraftRuns.Add((stepOrder, draft));
        Masking.Add(maskPii);

        return Task.FromResult(Answer());
    }
}
