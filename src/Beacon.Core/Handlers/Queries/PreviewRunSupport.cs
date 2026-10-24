using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Queries;

/// <summary>What the query and step preview handlers share: the authorization checks and the failure shape.</summary>
internal static class PreviewRunSupport
{
    // Checked before anything is read, outside the catch that turns failures into results: the stored query needs Run
    // (unreadable or missing is 404, a query without steps is its owner's), a draft's sources need Query (one project
    // holding all of them, or all openly reachable). A draft runs exactly the steps it carries.
    public static Task DemandMayRunAsync(ProjectRequestAuthorizer authorizer, int queryId, QueryDraft? draft, CancellationToken cancellationToken)
    {
        var check = draft == null
            ? new ProjectCheck(ProjectResource.Query(queryId), ProjectAction.Run)
            : new ProjectCheck(ProjectResource.DataSources(draft.Steps?.Select(x => x.DataSourceId)), ProjectAction.Query);

        return authorizer.DemandAsync(check, cancellationToken);
    }

    // The stored steps that run are the ones authorized here, right before their SQL: the query is read once, detached,
    // and the data sources of that snapshot need Run in one project holding all of them (or all openly reachable), so an
    // edit saved after the check above cannot run a step on a source the caller may not run. A snapshot without steps
    // runs no source SQL and needs nothing beyond that check.
    public static async Task<StoredQuerySnapshot> LoadRunnableSnapshotAsync(
        IStoredQueryPreview storedPreview,
        ProjectRequestAuthorizer authorizer,
        int queryId,
        CancellationToken cancellationToken)
    {
        var snapshot = await storedPreview.LoadSnapshot(queryId, cancellationToken);
        if (snapshot.Steps.Count > 0)
        {
            await authorizer.DemandAsync(new ProjectCheck(ProjectResource.DataSources(snapshot.DataSourceIds), ProjectAction.Run), cancellationToken);
        }

        return snapshot;
    }

    // A refusal is the answer (404 or 403, already logged), never a failed preview.
    public static bool IsAccessRefusal(Exception exception) =>
        exception is BeaconNotFoundException or BeaconForbiddenException;

    // A preview is run by the user who wrote the query, so the failure goes back to them as the result
    // (the shell renders ErrorMessage) instead of null, which the handlers could only turn into
    // "preview failed" - hiding the cause in a host log the user usually cannot see.
    public static QueryPreviewResult Failed(Exception exception) =>
        new() { Success = false, ErrorMessage = exception.Message };
}
