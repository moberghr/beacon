using Beacon.Core.Helpers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

public interface IQueryVersionService
{
    /// <summary>
    /// Snapshots the stored query as a new version, with its content hash. An Active version replaces the one that was
    /// live: every version still marked Active is archived, so a query has one active version.
    /// <paramref name="userId"/> is the author's <c>Users.ExternalId</c>.
    /// </summary>
    Task<QueryVersion> CreateVersionAsync(int queryId, string? userId, string? source, string? reason, QueryVersionStatus status, CancellationToken cancellationToken = default);
    /// <summary>
    /// Before an edit replaces the stored query, makes sure history already holds it. The active version normally
    /// does; an unattributed Archived baseline, with its content hash, is written only when there is none yet or the query
    /// was changed outside a versioned save, and no version that was live (Active or Archived) holds the same content.
    /// </summary>
    Task EnsureBaselineVersionAsync(int queryId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Submits <paramref name="proposed"/> — the change awaiting review, not the stored query, because approving applies
    /// it to the live query — as a PendingApproval version with its content hash and a pending approval request naming
    /// the requester, the other people who shaped it and the query's active version (the base the change was made on), in
    /// one transaction that first takes the query's row, as approving and rejecting do, so submissions and decisions on
    /// one query never interleave. One request per query waits for a decision: a submission replaces only its requester's
    /// own pending request (superseded, the requester notified), and is refused (409) while another requester's request
    /// waits — a reviewer approves or rejects that one first. When the stored SQL the change would replace is in no
    /// version yet, it is recorded first as an Archived baseline. Refused when there is no requester and the host does not
    /// allow self-approval (409: nobody could approve it), when the editor's base is no longer the active version (409),
    /// when another change to the query was written at the same moment (409), and when the steps' orders do not run 1, 2,
    /// 3, … or a step names a data source that does not exist. The live query is not touched.
    /// </summary>
    Task<QueryVersionChange> SubmitForApprovalAsync(int queryId, QueryData proposed, QueryChangeSubmission submission, CancellationToken cancellationToken = default);
    /// <summary>
    /// Creates <paramref name="query"/> (its name, description, owner, folder, AI actor) with nothing live — no steps, no
    /// final query — and submits <paramref name="proposed"/> as its first version (<see cref="SubmitForApprovalAsync"/>),
    /// in one transaction: a new query runs nothing until its content is approved. <paramref name="query"/> must not carry
    /// steps or a final query of its own.
    /// </summary>
    Task<QueryVersionChange> SubmitNewQueryAsync(Query query, QueryData proposed, QueryChangeSubmission submission, CancellationToken cancellationToken = default);
    /// <summary>
    /// The query's versions the caller may read (<paramref name="readable"/>), each by the data sources it recorded as
    /// its own detail is: one readable project holds all of them (or they are all openly reachable), and a version without
    /// any is its query owner's. Newest first unless <c>sort</c> says otherwise.
    /// </summary>
    Task<PagedList<QueryVersionSummary>> GetVersionsAsync(int queryId, ListRequest request, ProjectSet readable, CancellationToken cancellationToken);
    Task<QueryVersionDetail?> GetVersionDetailAsync(int versionId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Puts a version that was live (Active or Archived) back: never a pending, rejected or superseded proposal, which
    /// nobody approved. Its steps' orders and SQL pass the checks of a save again first. With the approval workflow on
    /// the restore is submitted for approval (<see cref="SubmitForApprovalAsync"/>, "Restore of version N") and the
    /// PendingApproval version is returned; otherwise it goes live as a new Active version, with the stored SQL it replaces
    /// recorded first when no version holds it. <paramref name="userId"/> is the restoring user's <c>Users.ExternalId</c>.
    /// </summary>
    Task<QueryVersionChange> RestoreVersionAsync(int versionId, string? userId, CancellationToken cancellationToken = default);
    Task<QueryVersionDiff> DiffVersionsAsync(int versionIdA, int versionIdB, CancellationToken cancellationToken = default);
}

internal class QueryVersionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext,
    BeaconConfiguration configuration,
    IStepSqlSaveCheck saveCheck,
    ILogger<QueryVersionService> logger,
    IApprovalNotifier? approvalNotifier = null) : IQueryVersionService
{
    /// <summary>The source a restore records on its version.</summary>
    public const string RestoreSource = "Restore";

    /// <summary>The source of a version that records the stored SQL a change replaced.</summary>
    public const string BaselineSource = "Baseline";

    private const string BaselineReason = "The query as stored before this edit";

    /// <summary>Why a submission without a requester is refused while self-approval is off.</summary>
    internal const string RequesterRequiredMessage =
        "A change waits for someone other than its requester to approve it, so Beacon must be able to name the requester: sign in as a Beacon user (an API key needs an active owner, an AI actor a creator).";

    /// <summary>Why a submission made on a version that is no longer active is refused.</summary>
    internal const string StaleEditorMessage =
        "The query changed after this edit was loaded: its active version is no longer the one the edit was made on. Reload the query and make the change again.";

    /// <summary>Why a submission is refused while another requester's change to the query waits for review.</summary>
    internal const string OtherPendingMessage =
        "Another change to this query is waiting for review; a reviewer must approve or reject it first.";

    /// <summary>Why a submission that lost a race with another write of the query's versions is refused.</summary>
    internal const string ConcurrentWriteMessage = "Another change to this query was written at the same moment. Submit the change again.";

    /// <summary>The status an <see cref="IApprovalNotifier"/> reports for a request a newer submission superseded.</summary>
    internal const string SupersededStatus = "superseded";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<QueryVersion> CreateVersionAsync(int queryId, string? userId, string? source, string? reason, QueryVersionStatus status, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = await context.Queries
            .Include(q => q.Steps)
                .ThenInclude(s => s.DataSource)
            .Include(q => q.Steps)
                .ThenInclude(s => s.Parameters)
            .Where(q => q.Id == queryId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query {queryId} not found.");

        // Get next version number
        var maxVersionNullable = await context.QueryVersions
            .Where(v => v.QueryId == queryId)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(cancellationToken);

        var maxVersion = maxVersionNullable ?? 0;
        var authorName = await ChangeAuthorLabel.OfAsync(context, userId, SessionDisplayNameOf(userId), cancellationToken);

        var version = new QueryVersion
        {
            QueryId = queryId,
            VersionNumber = maxVersion + 1,
            Status = status,
            Name = query.Name,
            Description = query.Description,
            FinalQuery = query.FinalQuery,
            StepsJson = SerializeSteps(query),
            DataSourceIds = RecordedDataSourceIds(query.Steps.Select(x => x.DataSourceId)),
            ContentHash = QueryContentHash.Of(query).Hash,
            CreatedByUserId = userId,
            CreatedByUserName = authorName,
            ChangeSource = source,
            ChangeReason = reason
        };

        // If this is the active version, it replaces whatever was live (older data can hold several), and the
        // query points at it via navigation so EF resolves the FK during the single SaveChanges below (§5.7).
        if (status == QueryVersionStatus.Active)
        {
            var previousActive = await context.QueryVersions
                .Where(v => v.QueryId == queryId)
                .Where(v => v.Status == QueryVersionStatus.Active)
                .ToListAsync(cancellationToken);

            foreach (var x in previousActive)
            {
                x.Status = QueryVersionStatus.Archived;
            }

            query.ActiveVersion = version;
        }

        context.QueryVersions.Add(version);

        await context.SaveChangesAsync(cancellationToken);
        return version;
    }

    public async Task EnsureBaselineVersionAsync(int queryId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = await context.Queries
            .Include(q => q.Steps)
                .ThenInclude(s => s.DataSource)
            .Include(q => q.Steps)
                .ThenInclude(s => s.Parameters)
            .Where(q => q.Id == queryId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query {queryId} not found.");

        var stepsJson = SerializeSteps(query);

        var active = await context.QueryVersions
            .Where(v => v.QueryId == queryId)
            .Where(v => v.Status == QueryVersionStatus.Active)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v =>
                new
                {
                    v.StepsJson,
                    v.FinalQuery
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (active != null && active.StepsJson == stepsJson && active.FinalQuery == query.FinalQuery)
        {
            return;
        }

        // A version that was live (Active or Archived) with the same content already holds it: no second baseline.
        var content = QueryContentHash.Of(query);
        var recorded = await context.QueryVersions
            .Where(x => x.QueryId == queryId)
            .Where(x => x.Status == QueryVersionStatus.Active || x.Status == QueryVersionStatus.Archived)
            .Where(x => x.ContentHash == content.Hash)
            .AnyAsync(cancellationToken);
        if (recorded)
        {
            return;
        }

        var maxVersionNullable = await context.QueryVersions
            .Where(v => v.QueryId == queryId)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(cancellationToken);

        context.QueryVersions.Add(new QueryVersion
        {
            QueryId = queryId,
            VersionNumber = (maxVersionNullable ?? 0) + 1,
            Status = QueryVersionStatus.Archived,
            Name = query.Name,
            Description = query.Description,
            FinalQuery = query.FinalQuery,
            StepsJson = stepsJson,
            DataSourceIds = RecordedDataSourceIds(query.Steps.Select(x => x.DataSourceId)),
            ContentHash = content.Hash,
            ChangeSource = BaselineSource,
            ChangeReason = BaselineReason
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<QueryVersionChange> SubmitForApprovalAsync(int queryId, QueryData proposed, QueryChangeSubmission submission, CancellationToken cancellationToken = default)
    {
        EnsureRequester(submission);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var proposedVersion = await ProposedVersionOfAsync(context, proposed, cancellationToken);

        return await SubmitAsync(context, queryId, null, proposedVersion, submission, cancellationToken);
    }

    public async Task<QueryVersionChange> SubmitNewQueryAsync(Query query, QueryData proposed, QueryChangeSubmission submission, CancellationToken cancellationToken = default)
    {
        if (query.Id != 0 || query.Steps.Count > 0 || !string.IsNullOrEmpty(query.FinalQuery))
        {
            throw new ArgumentException("A query submitted for approval is created without steps or a final query of its own: they are the proposal's.", nameof(query));
        }

        EnsureRequester(submission);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var proposedVersion = await ProposedVersionOfAsync(context, proposed, cancellationToken);

        return await SubmitAsync(context, 0, query, proposedVersion, submission, cancellationToken);
    }

    public async Task<PagedList<QueryVersionSummary>> GetVersionsAsync(int queryId, ListRequest request, ProjectSet readable, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Versions recorded before Beacon kept their sources are decided from their snapshots before paging, so the
        // page and its total count leave out the same unreadable versions.
        var unreadableLegacy = readable.Ids == null
            ? []
            : await UnreadableLegacyVersionIdsAsync(context, queryId, readable, cancellationToken);

        // StepCount is parsed from StepsJson, so the page is mapped after it is read.
        var page = await context.QueryVersions
            .Where(v => v.QueryId == queryId)
            .WhereVersionSourcesAccessible(context, readable, x => x.DataSourceIds, x => x.Query.OwnerUserId)
            .WhereIf(unreadableLegacy.Count > 0, x => !unreadableLegacy.Contains(x.Id))
            .Select(v => new VersionRow
            {
                Id = v.Id,
                VersionNumber = v.VersionNumber,
                Label = v.Label,
                Status = v.Status,
                Name = v.Name,
                CreatedTime = v.CreatedTime,
                // Named to other users by display name or "User {id}", never by user name or external id.
                CreatedByUserName = context.Users
                    .Where(u => u.ExternalId == v.CreatedByUserId)
                    .Select(UserLabel.OfUser)
                    .FirstOrDefault(),
                ChangeSource = v.ChangeSource,
                ChangeReason = v.ChangeReason,
                StepsJson = v.StepsJson
            })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-versionNumber");

        return page.Map(v => new QueryVersionSummary
        {
            Id = v.Id,
            VersionNumber = v.VersionNumber,
            Label = v.Label,
            Status = v.Status,
            Name = v.Name,
            CreatedTime = v.CreatedTime,
            CreatedByUserName = v.CreatedByUserName,
            ChangeSource = v.ChangeSource,
            ChangeReason = v.ChangeReason,
            StepCount = CountSteps(v.StepsJson)
        });
    }

    public async Task<QueryVersionDetail?> GetVersionDetailAsync(int versionId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var version = await context.QueryVersions
            .Where(v => v.Id == versionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (version == null) return null;

        var authors = await AuthorLabelsAsync(context, [version.CreatedByUserId], cancellationToken);

        return ToDetail(version, authors);
    }

    public async Task<QueryVersionChange> RestoreVersionAsync(int versionId, string? userId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var version = await context.QueryVersions
            .Where(v => v.Id == versionId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query version {versionId} not found.");

        // A pending, rejected or superseded proposal was never approved: restoring it would make it live unreviewed.
        if (version.Status is QueryVersionStatus.PendingApproval or QueryVersionStatus.Rejected or QueryVersionStatus.Superseded)
        {
            throw new BeaconConflictException("Only a version that was live can be restored; this version was never approved.");
        }

        var snapshots = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(version.StepsJson, JsonOptions) ?? [];

        // The snapshot is checked again, as an edit is: its steps' orders, and its SQL (the gate, the host policy or the
        // source's metadata may have changed since it was live, and its data sources must still exist).
        var orders = QueryStepOrders.RefusalFor(snapshots.Select(x => x.StepOrder));
        if (orders != null)
        {
            throw new BeaconConflictException($"This version cannot be restored: {orders}");
        }

        await EnsureStepsPassTheSaveCheckAsync(snapshots, cancellationToken);

        var reason = $"Restore of version {version.VersionNumber}";
        if (configuration.ApprovalWorkflow.Enabled)
        {
            var submission = new QueryChangeSubmission(userId, SessionDisplayNameOf(userId), RestoreSource, reason, reason);
            EnsureRequester(submission);

            return await SubmitAsync(
                context,
                version.QueryId,
                null,
                new ProposedVersion(version.Name, version.Description, version.FinalQuery, snapshots),
                submission,
                cancellationToken);
        }

        var query = await context.Queries
            .Include(q => q.Steps)
                .ThenInclude(s => s.DataSource)
            .Include(q => q.Steps)
                .ThenInclude(s => s.Parameters)
            .Where(q => q.Id == version.QueryId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query {version.QueryId} not found.");

        var maxVersionNumber = await context.QueryVersions
            .Where(v => v.QueryId == query.Id)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(cancellationToken) ?? 0;
        var restoredHash = QueryContentHash.Of(version.FinalQuery, snapshots).Hash;

        // History must hold the stored SQL the restore replaces: recorded first when no version holds it, in this save.
        var baseline = await UnrecordedBaselineAsync(context, query, restoredHash, maxVersionNumber + 1, cancellationToken);
        if (baseline != null)
        {
            maxVersionNumber = baseline.VersionNumber;
        }

        // Archive the current active version (older data can hold several)
        var currentActive = await context.QueryVersions
            .Where(v => v.QueryId == query.Id)
            .Where(v => v.Status == QueryVersionStatus.Active)
            .ToListAsync(cancellationToken);

        foreach (var x in currentActive)
        {
            x.Status = QueryVersionStatus.Archived;
        }

        // Apply snapshot to live query
        query.Name = version.Name;
        query.Description = version.Description;
        query.FinalQuery = version.FinalQuery;

        // Remove existing steps and parameters (cascaded by EF when we remove
        // the step; parameters were configured with cascade-on-delete).
        foreach (var step in query.Steps.ToList())
        {
            foreach (var param in step.Parameters.ToList())
            {
                context.QueryStepParameters.Remove(param);
            }
            context.QuerySteps.Remove(step);
        }

        // Recreate steps from snapshot using navigation so EF assigns FKs
        // for both QueryStep and its child QueryStepParameters in one save.
        foreach (var snapshot in snapshots)
        {
            context.QuerySteps.Add(ToStep(query.Id, snapshot));
        }

        var authorName = await ChangeAuthorLabel.OfAsync(context, userId, SessionDisplayNameOf(userId), cancellationToken);

        var newVersion = new QueryVersion
        {
            QueryId = query.Id,
            VersionNumber = maxVersionNumber + 1,
            Status = QueryVersionStatus.Active,
            Name = version.Name,
            Description = version.Description,
            FinalQuery = version.FinalQuery,
            StepsJson = version.StepsJson,
            DataSourceIds = RecordedDataSourceIds(snapshots.Select(x => x.DataSourceId)),
            ContentHash = restoredHash,
            CreatedByUserId = userId,
            CreatedByUserName = authorName,
            ChangeSource = RestoreSource,
            ChangeReason = $"Restored from version {version.VersionNumber}"
        };

        if (baseline != null)
        {
            context.QueryVersions.Add(baseline);
        }

        context.QueryVersions.Add(newVersion);
        query.ActiveVersion = newVersion;

        await context.SaveChangesAsync(cancellationToken);

        return new QueryVersionChange(newVersion, 0);
    }

    /// <summary>
    /// A live step recreated from a version's snapshot, with its parameters on the navigation so EF assigns the keys in
    /// the same save.
    /// </summary>
    internal static QueryStep ToStep(int queryId, QueryStepSnapshot snapshot)
    {
        return new QueryStep
        {
            QueryId = queryId,
            StepOrder = snapshot.StepOrder,
            SqlValue = snapshot.SqlValue,
            DataSourceId = snapshot.DataSourceId,
            Name = snapshot.Name,
            Description = snapshot.Description,
            Parameters = snapshot.Parameters
                .Select(x =>
                    new QueryStepParameter
                    {
                        // QueryStepId is satisfied by the parent navigation collection; EF resolves it during SaveChanges.
                        QueryStepId = 0,
                        Name = x.Name,
                        Type = x.Type,
                        Description = x.Description,
                        Placeholder = x.Placeholder
                    })
                .ToList()
        };
    }

    public async Task<QueryVersionDiff> DiffVersionsAsync(int versionIdA, int versionIdB, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var versions = await context.QueryVersions
            .Where(v => v.Id == versionIdA || v.Id == versionIdB)
            .ToListAsync(cancellationToken);

        var versionA = versions.FirstOrDefault(v => v.Id == versionIdA)
            ?? throw new InvalidOperationException($"Query version {versionIdA} not found.");
        var versionB = versions.FirstOrDefault(v => v.Id == versionIdB)
            ?? throw new InvalidOperationException($"Query version {versionIdB} not found.");

        var authors = await AuthorLabelsAsync(context, [versionA.CreatedByUserId, versionB.CreatedByUserId], cancellationToken);
        var detailA = ToDetail(versionA, authors);
        var detailB = ToDetail(versionB, authors);

        var stepDiffs = ComputeStepDiffs(detailA.Steps, detailB.Steps);

        return new QueryVersionDiff
        {
            VersionA = detailA,
            VersionB = detailB,
            NameChanged = detailA.Name != detailB.Name,
            DescriptionChanged = detailA.Description != detailB.Description,
            FinalQueryChanged = detailA.FinalQuery != detailB.FinalQuery,
            StepDiffs = stepDiffs
        };
    }

    // The display name a caller's session presents, recorded for userId only when it is that caller (a host without
    // Beacon user management has no stored user to label); never another person's.
    private string? SessionDisplayNameOf(string? userId) =>
        userId != null && userId == userContext.UserId ? userContext.DisplayName : null;

    // A submission nobody could approve is refused before anything is written: with self-approval off, the requester must
    // be someone a reviewer can be told apart from.
    private void EnsureRequester(QueryChangeSubmission submission)
    {
        if (submission.RequesterUserId == null && !configuration.ApprovalWorkflow.AllowSelfApproval)
        {
            throw new BeaconConflictException(RequesterRequiredMessage);
        }
    }

    // The version an edit proposes: its steps in run order, each naming an existing data source (approval recreates the
    // steps from this snapshot, so an unknown one has to fail now, not then), with orders that run 1, 2, 3, ….
    private static async Task<ProposedVersion> ProposedVersionOfAsync(BeaconContext context, QueryData proposed, CancellationToken cancellationToken)
    {
        var orders = QueryStepOrders.RefusalFor(proposed.Steps.Select(x => x.StepOrder));
        if (orders != null)
        {
            throw new InvalidOperationException(orders);
        }

        var dataSourceIds = proposed.Steps
            .Select(x => x.DataSourceId)
            .Distinct()
            .ToList();

        var dataSourceNames = await context.DataSources
            .Where(x => dataSourceIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var missing = dataSourceIds.FirstOrDefault(x => !dataSourceNames.ContainsKey(x), -1);
        if (missing != -1)
        {
            throw new InvalidOperationException($"Data source {missing} not found.");
        }

        var snapshots = proposed.Steps
            .OrderBy(x => x.StepOrder)
            .Select(x =>
                new QueryStepSnapshot
                {
                    StepOrder = x.StepOrder,
                    SqlValue = x.SqlValue,
                    DataSourceId = x.DataSourceId,
                    DataSourceName = dataSourceNames[x.DataSourceId],
                    Name = x.Name,
                    Description = x.Description,
                    Parameters = x.Parameters
                        .Select(y =>
                            new QueryStepParameterSnapshot
                            {
                                Name = y.Name,
                                Type = y.Type,
                                Description = y.Description,
                                Placeholder = y.Placeholder
                            })
                        .ToList()
                })
            .ToList();

        return new ProposedVersion(proposed.Name, proposed.Description, proposed.FinalQuery, snapshots);
    }

    // One unit of work, serialised per query: the query's row is taken first (as approving and rejecting take it), so a
    // concurrent submission or decision waits here instead of interleaving; the active version is read after it. The
    // requester's own earlier pending proposals are superseded (anyone else's refuses the submission), the stored SQL the
    // change replaces is recorded when no version holds it, and the new version and its request are written with the base
    // they were made on, so two submissions never leave two requests both waiting. newQuery: a query created with the
    // submission (nothing to take, supersede or record).
    private async Task<QueryVersionChange> SubmitAsync(
        BeaconContext context,
        int queryId,
        Query? newQuery,
        ProposedVersion proposed,
        QueryChangeSubmission submission,
        CancellationToken cancellationToken)
    {
        var proposal = new Proposal(
            proposed,
            submission,
            await ChangeAuthorLabel.OfAsync(context, submission.RequesterUserId, submission.RequesterDisplayName, cancellationToken),
            QueryContentHash.Of(proposed.FinalQuery, proposed.Steps).Hash);

        // Through the execution strategy, so a host that enables retry-on-failure gets a retried unit instead of an
        // "execution strategy does not support user-initiated transactions" error; a retry starts from a clean tracker.
        var strategy = context.Database.CreateExecutionStrategy();

        try
        {
            var change = await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                if (newQuery != null)
                {
                    // The query and its first proposal in one save: EF keys the version and the request through the
                    // query's navigations (assigned, not added, so a retried unit writes one version).
                    var first = proposal.ToVersion(0, 1);
                    newQuery.Versions = [first];

                    context.Queries.Add(newQuery);
                    context.QueryApprovalRequests.Add(proposal.ToRequest(first, newQuery, null));
                    await context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    return new SubmittedChange(first, []);
                }

                var locked = await context.Queries
                    .Where(x => x.Id == queryId)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.ActiveVersionId, y => y.ActiveVersionId), cancellationToken);
                if (locked == 0)
                {
                    throw new InvalidOperationException($"Query {queryId} not found.");
                }

                var live = await context.Queries
                    .Where(x => x.Id == queryId)
                    .Select(x =>
                        new LiveQuery(
                            x.ActiveVersionId,
                            x.Name,
                            x.Description,
                            x.FinalQuery))
                    .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new InvalidOperationException($"Query {queryId} not found.");

                if (submission.ExpectedBaseVersionId != null && submission.ExpectedBaseVersionId != live.ActiveVersionId)
                {
                    throw new BeaconConflictException(StaleEditorMessage);
                }

                var pending = await context.QueryApprovalRequests
                    .Where(x => x.QueryId == queryId)
                    .Where(x => x.Status == ApprovalStatus.Pending)
                    .Select(x =>
                        new SupersededRequest(
                            x.Id,
                            x.RequestedByUserId))
                    .ToListAsync(cancellationToken);

                // A submission replaces only its requester's own pending request: while anyone else's waits (or one whose
                // requester Beacon could not name), it is refused, so nobody's change is withdrawn by another person's save.
                if (pending.Any(x => !IsSameRequester(x.RequestedByUserId, submission.RequesterUserId)))
                {
                    throw new BeaconConflictException(OtherPendingMessage);
                }

                // Every pending request is the requester's own from here (the row lock keeps new ones out): superseding
                // the query's pending requests supersedes exactly those.
                await context.QueryVersions
                    .Where(x => x.QueryId == queryId)
                    .Where(x => x.Status == QueryVersionStatus.PendingApproval)
                    .Where(x => context.QueryApprovalRequests
                        .Where(y => y.QueryVersionId == x.Id)
                        .Any(y => y.Status == ApprovalStatus.Pending))
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, QueryVersionStatus.Superseded), cancellationToken);

                await context.QueryApprovalRequests
                    .Where(x => x.QueryId == queryId)
                    .Where(x => x.Status == ApprovalStatus.Pending)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, ApprovalStatus.Superseded), cancellationToken);

                var maxVersionNumber = await context.QueryVersions
                    .Where(x => x.QueryId == queryId)
                    .Select(x => (int?)x.VersionNumber)
                    .MaxAsync(cancellationToken) ?? 0;

                // History must hold the stored SQL an approval of this change would replace.
                var liveSteps = await context.QuerySteps
                    .AsNoTracking()
                    .Include(x => x.DataSource)
                    .Include(x => x.Parameters)
                    .Where(x => x.QueryId == queryId)
                    .ToListAsync(cancellationToken);
                var stored = new Query
                {
                    Id = queryId,
                    Name = live.Name,
                    Description = live.Description,
                    FinalQuery = live.FinalQuery,
                    Steps = liveSteps
                };
                var baseline = await UnrecordedBaselineAsync(context, stored, proposal.ContentHash, maxVersionNumber + 1, cancellationToken);
                var version = proposal.ToVersion(queryId, (baseline?.VersionNumber ?? maxVersionNumber) + 1);

                if (baseline != null)
                {
                    context.QueryVersions.Add(baseline);
                }

                context.QueryVersions.Add(version);
                context.QueryApprovalRequests.Add(proposal.ToRequest(version, null, live.ActiveVersionId));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new SubmittedChange(version, pending);
            });

            await NotifySupersededAsync(change.Superseded, cancellationToken);

            return new QueryVersionChange(change.Version, change.Superseded.Count);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            // Another write of the query's versions took the same version number between the read and this insert (a save
            // or restore with the workflow off does not take the query's row): nothing was written.
            logger.LogWarning("Submission for query {QueryId} lost a race for its version number and was refused", queryId);

            throw new BeaconConflictException(ConcurrentWriteMessage);
        }
    }

    // The same person by the id a request recorded for them (ignoring case, as identity providers may change it).
    private static bool IsSameRequester(string? recorded, string? requester)
    {
        return recorded != null && requester != null && string.Equals(recorded, requester, StringComparison.OrdinalIgnoreCase);
    }

    // Tells each superseded request's requester, after the submission committed; a failed push is logged, never a failed
    // submission.
    private async Task NotifySupersededAsync(IReadOnlyList<SupersededRequest> superseded, CancellationToken cancellationToken)
    {
        if (superseded.Count == 0)
        {
            return;
        }

        if (approvalNotifier == null)
        {
            logger.LogWarning(
                "{RequestCount} approval requests were superseded but no IApprovalNotifier is registered, so no requester was notified. Call AddBeaconApiServices() to enable push.",
                superseded.Count);

            return;
        }

        foreach (var request in superseded)
        {
            try
            {
                await approvalNotifier.ApprovalUpdatedAsync(request.Id, SupersededStatus, null, request.RequestedByUserId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("The requester of superseded approval request {RequestId} could not be notified ({ExceptionType})", request.Id, ex.GetType().Name);
            }
        }
    }

    // The stored SQL a change would replace, as an Archived baseline numbered versionNumber, when it is not empty, differs
    // from the change and no live-again version (Active or Archived) of the query holds it already; otherwise null.
    private static async Task<QueryVersion?> UnrecordedBaselineAsync(
        BeaconContext context,
        Query live,
        string replacedByHash,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        var content = QueryContentHash.Of(live);
        if (content.IsEmpty || string.Equals(content.Hash, replacedByHash, StringComparison.Ordinal))
        {
            return null;
        }

        var recorded = await context.QueryVersions
            .Where(x => x.QueryId == live.Id)
            .Where(x => x.Status == QueryVersionStatus.Active || x.Status == QueryVersionStatus.Archived)
            .Where(x => x.ContentHash == content.Hash)
            .AnyAsync(cancellationToken);
        if (recorded)
        {
            return null;
        }

        return new QueryVersion
        {
            QueryId = live.Id,
            VersionNumber = versionNumber,
            Status = QueryVersionStatus.Archived,
            Name = live.Name,
            Description = live.Description,
            FinalQuery = live.FinalQuery,
            StepsJson = SerializeSteps(live),
            DataSourceIds = RecordedDataSourceIds(live.Steps.Select(x => x.DataSourceId)),
            ContentHash = content.Hash,
            ChangeSource = BaselineSource,
            ChangeReason = BaselineReason
        };
    }

    // Every step's SQL passes the save check (the gate and the source's own tables and columns) with its placeholders.
    private async Task EnsureStepsPassTheSaveCheckAsync(IEnumerable<QueryStepSnapshot> steps, CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            var placeholders = step.Parameters
                .Select(x => x.Placeholder)
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!)
                .ToList();
            var rejection = await saveCheck.RejectionAsync(step.DataSourceId, step.SqlValue, placeholders, cancellationToken);
            if (rejection != null)
            {
                throw new BeaconConflictException($"Step {step.StepOrder} cannot be restored: {rejection}");
            }
        }
    }

    // The distinct data sources a version's steps read, recorded with the version so access to it is decided without
    // parsing StepsJson.
    internal static int[] RecordedDataSourceIds(IEnumerable<int> dataSourceIds)
    {
        return dataSourceIds
            .Distinct()
            .Order()
            .ToArray();
    }

    private static string SerializeSteps(Query query)
    {
        var snapshots = query.Steps
            .OrderBy(x => x.StepOrder)
            .Select(x =>
                new QueryStepSnapshot
                {
                    StepOrder = x.StepOrder,
                    SqlValue = x.SqlValue,
                    DataSourceId = x.DataSourceId,
                    DataSourceName = x.DataSource.Name,
                    Name = x.Name,
                    Description = x.Description,
                    Parameters = x.Parameters
                        .Select(y =>
                            new QueryStepParameterSnapshot
                            {
                                Name = y.Name,
                                Type = y.Type,
                                Description = y.Description,
                                Placeholder = y.Placeholder
                            })
                        .ToList()
                })
            .ToList();

        return JsonSerializer.Serialize(snapshots, JsonOptions);
    }

    // The authors' labels by external id: display name, or "User {id}" without one (never the user name or external id).
    private static async Task<Dictionary<string, string>> AuthorLabelsAsync(
        BeaconContext context,
        IReadOnlyCollection<string?> externalIds,
        CancellationToken cancellationToken)
    {
        var ids = externalIds
            .OfType<string>()
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var authors = await context.Users
            .Where(x => ids.Contains(x.ExternalId))
            .Select(x =>
                new
                {
                    x.ExternalId,
                    x.Id,
                    x.DisplayName
                })
            .ToListAsync(cancellationToken);

        return authors.ToDictionary(x => x.ExternalId, x => UserLabel.Of(x.Id, x.DisplayName));
    }

    private static QueryVersionDetail ToDetail(QueryVersion version, IReadOnlyDictionary<string, string> authors)
    {
        var steps = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(version.StepsJson, JsonOptions) ?? [];

        return new QueryVersionDetail
        {
            Id = version.Id,
            VersionNumber = version.VersionNumber,
            Label = version.Label,
            Status = version.Status,
            Name = version.Name,
            Description = version.Description,
            FinalQuery = version.FinalQuery,
            CreatedTime = version.CreatedTime,
            CreatedByUserName = version.CreatedByUserId == null ? null : authors.GetValueOrDefault(version.CreatedByUserId),
            ChangeSource = version.ChangeSource,
            ChangeReason = version.ChangeReason,
            Steps = steps
        };
    }

    // Steps are paired by their place in the run (step order, then as listed), never by the order value alone: two steps
    // sharing an order (data written before orders were checked) are both shown, and a swap shows as two changes.
    private static List<StepDiff> ComputeStepDiffs(List<QueryStepSnapshot> stepsA, List<QueryStepSnapshot> stepsB)
    {
        var diffs = new List<StepDiff>();
        var runA = stepsA
            .OrderBy(x => x.StepOrder)
            .ToList();
        var runB = stepsB
            .OrderBy(x => x.StepOrder)
            .ToList();

        for (var i = 0; i < Math.Max(runA.Count, runB.Count); i++)
        {
            var order = i + 1;
            var stepA = i < runA.Count ? runA[i] : null;
            var stepB = i < runB.Count ? runB[i] : null;

            if (stepA != null && stepB == null)
            {
                diffs.Add(new StepDiff { StepOrder = order, DiffType = StepDiffType.Removed, StepA = stepA });
            }
            else if (stepA == null && stepB != null)
            {
                diffs.Add(new StepDiff { StepOrder = order, DiffType = StepDiffType.Added, StepB = stepB });
            }
            else if (stepA != null && stepB != null)
            {
                var sqlChanged = stepA.SqlValue != stepB.SqlValue;
                var dsChanged = stepA.DataSourceId != stepB.DataSourceId;
                var nameChanged = stepA.Name != stepB.Name;
                // A parameter's name, type or placeholder changes what the step binds, as its SQL does.
                var parametersChanged = !QueryContentHash.Of(null, [stepA]).SameAs(QueryContentHash.Of(null, [stepB]));

                diffs.Add(new StepDiff
                {
                    StepOrder = order,
                    DiffType = (sqlChanged || dsChanged || nameChanged || parametersChanged) ? StepDiffType.Modified : StepDiffType.Unchanged,
                    StepA = stepA,
                    StepB = stepB
                });
            }
        }

        return diffs;
    }

    private int CountSteps(string stepsJson)
    {
        try
        {
            var steps = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(stepsJson, JsonOptions);
            return steps?.Count ?? 0;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize query version steps; treating as zero-step version");
            return 0;
        }
    }

    // The query's versions recorded before Beacon kept their sources that the caller may not read by the sources their
    // snapshots name: one read of the snapshots and at most one of the links, whatever the number of versions.
    private static async Task<List<int>> UnreadableLegacyVersionIdsAsync(
        BeaconContext context,
        int queryId,
        ProjectSet readable,
        CancellationToken cancellationToken)
    {
        var legacy = await context.QueryVersions
            .Where(x => x.QueryId == queryId)
            .Where(x => x.DataSourceIds == null)
            .Select(x =>
                new
                {
                    x.Id,
                    x.StepsJson,
                    QueryOwnerUserId = x.Query.OwnerUserId
                })
            .ToListAsync(cancellationToken);
        if (legacy.Count == 0)
        {
            return [];
        }

        var sources = legacy
            .Select(x => QueryVersionSources.Of(null, x.StepsJson))
            .ToList();
        var reachable = await ProjectSourceFootprint.AllowEachAsync(
            context,
            readable,
            sources
                .Select(x => x ?? [])
                .ToList(),
            cancellationToken);

        return legacy
            .Where((x, i) => !QueryVersionSources.IsReadable(readable, sources[i], reachable[i], x.QueryOwnerUserId))
            .Select(x => x.Id)
            .ToList();
    }

    // A version to propose: what approving it applies to the live query.
    private sealed record ProposedVersion(string Name, string? Description, string? FinalQuery, IReadOnlyList<QueryStepSnapshot> Steps);

    // A submission as it is written: the proposed version and its pending request.
    private sealed record Proposal(ProposedVersion Proposed, QueryChangeSubmission Submission, string? RequesterName, string ContentHash)
    {
        public QueryVersion ToVersion(int queryId, int versionNumber)
        {
            return new QueryVersion
            {
                QueryId = queryId,
                VersionNumber = versionNumber,
                Status = QueryVersionStatus.PendingApproval,
                Name = Proposed.Name,
                Description = Proposed.Description,
                FinalQuery = Proposed.FinalQuery,
                StepsJson = JsonSerializer.Serialize(Proposed.Steps, JsonOptions),
                DataSourceIds = RecordedDataSourceIds(Proposed.Steps.Select(x => x.DataSourceId)),
                ContentHash = ContentHash,
                CreatedByUserId = Submission.RequesterUserId,
                CreatedByUserName = RequesterName,
                ChangeSource = Submission.Source,
                ChangeReason = Submission.Reason
            };
        }

        // newQuery keys the request of a query created with the submission; an existing query's is keyed by its id.
        public QueryApprovalRequest ToRequest(QueryVersion version, Query? newQuery, int? baseVersionId)
        {
            var contributors = Submission.ContributorUserIds
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new QueryApprovalRequest
            {
                QueryId = version.QueryId,
                Query = newQuery!,
                QueryVersion = version,
                Status = ApprovalStatus.Pending,
                RequestedByUserId = Submission.RequesterUserId,
                RequestedByUserName = RequesterName,
                ChangeSummary = Submission.Summary,
                BaseVersionId = baseVersionId,
                ContributorUserIds = contributors.Length == 0 ? null : contributors
            };
        }
    }

    // What one submission wrote: the proposal, and the pending requests it superseded.
    private sealed record SubmittedChange(QueryVersion Version, IReadOnlyList<SupersededRequest> Superseded);

    // The stored query a submission reads after taking its row: the base, and what a baseline records with its steps.
    private sealed record LiveQuery(int? ActiveVersionId, string Name, string? Description, string? FinalQuery);

    private sealed record SupersededRequest(int Id, string? RequestedByUserId);

    private sealed class VersionRow
    {
        public int Id { get; init; }

        public int VersionNumber { get; init; }

        public string? Label { get; init; }

        public QueryVersionStatus Status { get; init; }

        public string Name { get; init; } = string.Empty;

        public DateTime CreatedTime { get; init; }

        public string? CreatedByUserName { get; init; }

        public string? ChangeSource { get; init; }

        public string? ChangeReason { get; init; }

        public string StepsJson { get; init; } = string.Empty;
    }
}
