using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;

namespace Beacon.Tests.Common;

/// <summary>
/// The stored-query preview seam for preview-service tests: loads a snapshot without steps, answers every run with
/// <see cref="Answer"/> (which may throw, as a failed run does) and records what was loaded and run, and the temporary
/// queries it was asked to store (refusing them, so a temporary preview stops there).
/// </summary>
internal sealed class RecordingStoredQueryPreview : IStoredQueryPreview
{
    public Func<QueryPreviewResult> Answer { get; init; } = () => new QueryPreviewResult { Success = true };

    public List<int> Loads { get; } = [];

    /// <summary>Each run: the step it ran on its own (null for the whole query) and the page it asked for.</summary>
    public List<(int? StepOrder, ListRequest Paging)> Runs { get; } = [];

    /// <summary>The temporary queries a preview of unsaved steps asked to store.</summary>
    public List<QueryData> TemporaryQueries { get; } = [];

    public Task<BaseResponse> CreateTemporaryQuery(QueryData queryData, CancellationToken cancellationToken)
    {
        TemporaryQueries.Add(queryData);

        return Task.FromResult(new BaseResponse { Success = false, Message = "Not stored in a test." });
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
