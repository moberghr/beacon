using Beacon.Core.Helpers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

public interface IQueryVersionService
{
    /// <summary>
    /// Snapshots the stored query as a new version. An Active version replaces the one that was live: every version
    /// still marked Active is archived, so a query has one active version.
    /// </summary>
    Task<QueryVersion> CreateVersionAsync(int queryId, string? userId, string? source, string? reason, QueryVersionStatus status, CancellationToken cancellationToken = default);
    /// <summary>
    /// Before an edit replaces the stored query, makes sure history already holds it. The active version normally
    /// does; an unattributed Archived baseline is written only when there is none yet or the query was changed
    /// outside a versioned save.
    /// </summary>
    Task EnsureBaselineVersionAsync(int queryId, CancellationToken cancellationToken = default);
    /// <summary>
    /// A PendingApproval version holding <paramref name="proposed"/> — the edit awaiting review — rather than the
    /// stored query, because approving a version applies its steps to the live query.
    /// </summary>
    Task<QueryVersion> CreateProposedVersionAsync(int queryId, QueryData proposed, string? userId, string? source, string? reason, CancellationToken cancellationToken = default);
    Task<PagedList<QueryVersionSummary>> GetVersionsAsync(int queryId, ListRequest request, CancellationToken cancellationToken);
    Task<QueryVersionDetail?> GetVersionDetailAsync(int versionId, CancellationToken cancellationToken = default);
    Task<int> RestoreVersionAsync(int versionId, string? userId, CancellationToken cancellationToken = default);
    Task<QueryVersionDiff> DiffVersionsAsync(int versionIdA, int versionIdB, CancellationToken cancellationToken = default);
}

internal class QueryVersionService(IDbContextFactory<BeaconContext> contextFactory, IBeaconUserContext userContext, ILogger<QueryVersionService> logger) : IQueryVersionService
{
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

        var version = new QueryVersion
        {
            QueryId = queryId,
            VersionNumber = maxVersion + 1,
            Status = status,
            Name = query.Name,
            Description = query.Description,
            FinalQuery = query.FinalQuery,
            StepsJson = SerializeSteps(query),
            CreatedByUserId = userId,
            CreatedByUserName = AuthorName(userId),
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
            ChangeSource = "Baseline",
            ChangeReason = "The query as stored before this edit"
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<QueryVersion> CreateProposedVersionAsync(int queryId, QueryData proposed, string? userId, string? source, string? reason, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSourceIds = proposed.Steps
            .Select(x => x.DataSourceId)
            .Distinct()
            .ToList();

        var dataSourceNames = await context.DataSources
            .Where(x => dataSourceIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        // Approval recreates steps from this snapshot, so an unknown data source has to fail now, not then.
        var missing = dataSourceIds.FirstOrDefault(x => !dataSourceNames.ContainsKey(x), -1);
        if (missing != -1)
        {
            throw new InvalidOperationException($"Data source {missing} not found.");
        }

        var maxVersionNullable = await context.QueryVersions
            .Where(v => v.QueryId == queryId)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(cancellationToken);

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

        var version = new QueryVersion
        {
            QueryId = queryId,
            VersionNumber = (maxVersionNullable ?? 0) + 1,
            Status = QueryVersionStatus.PendingApproval,
            Name = proposed.Name,
            Description = proposed.Description,
            FinalQuery = proposed.FinalQuery,
            StepsJson = JsonSerializer.Serialize(snapshots, JsonOptions),
            CreatedByUserId = userId,
            CreatedByUserName = AuthorName(userId),
            ChangeSource = source,
            ChangeReason = reason
        };

        context.QueryVersions.Add(version);

        await context.SaveChangesAsync(cancellationToken);
        return version;
    }

    public async Task<PagedList<QueryVersionSummary>> GetVersionsAsync(int queryId, ListRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // StepCount is parsed from StepsJson, so the page is mapped after it is read.
        var page = await context.QueryVersions
            .Where(v => v.QueryId == queryId)
            .Select(v => new VersionRow
            {
                Id = v.Id,
                VersionNumber = v.VersionNumber,
                Label = v.Label,
                Status = v.Status,
                Name = v.Name,
                CreatedTime = v.CreatedTime,
                CreatedByUserId = v.CreatedByUserId,
                CreatedByUserName = v.CreatedByUserName,
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
            CreatedByUserId = v.CreatedByUserId,
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

        return ToDetail(version);
    }

    public async Task<int> RestoreVersionAsync(int versionId, string? userId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var version = await context.QueryVersions
            .Where(v => v.Id == versionId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query version {versionId} not found.");

        var query = await context.Queries
            .Include(q => q.Steps)
                .ThenInclude(s => s.Parameters)
            .Where(q => q.Id == version.QueryId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query {version.QueryId} not found.");

        var snapshots = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(version.StepsJson, JsonOptions) ?? [];

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
            context.QuerySteps.Add(new QueryStep
            {
                QueryId = query.Id,
                StepOrder = snapshot.StepOrder,
                SqlValue = snapshot.SqlValue,
                DataSourceId = snapshot.DataSourceId,
                Name = snapshot.Name,
                Description = snapshot.Description,
                Parameters = snapshot.Parameters
                    .Select(x => new QueryStepParameter
                    {
                        // QueryStepId is satisfied by the parent navigation
                        // collection; EF resolves it during SaveChanges.
                        QueryStepId = 0,
                        Name = x.Name,
                        Type = x.Type,
                        Description = x.Description,
                        Placeholder = x.Placeholder
                    })
                    .ToList()
            });
        }

        var maxVersionNumberNullable = await context.QueryVersions
            .Where(v => v.QueryId == query.Id)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(cancellationToken);
        var maxVersionNumber = maxVersionNumberNullable ?? 0;

        var newVersion = new QueryVersion
        {
            QueryId = query.Id,
            VersionNumber = maxVersionNumber + 1,
            Status = QueryVersionStatus.Active,
            Name = version.Name,
            Description = version.Description,
            FinalQuery = version.FinalQuery,
            StepsJson = version.StepsJson,
            CreatedByUserId = userId,
            CreatedByUserName = AuthorName(userId),
            ChangeSource = "Restore",
            ChangeReason = $"Restored from version {version.VersionNumber}"
        };

        context.QueryVersions.Add(newVersion);
        query.ActiveVersion = newVersion;

        await context.SaveChangesAsync(cancellationToken);

        return newVersion.VersionNumber;
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

        var detailA = ToDetail(versionA);
        var detailB = ToDetail(versionB);

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

    // A version with an author id was made by the user behind this request. A snapshot without one (the
    // pre-edit baseline) holds someone else's earlier SQL, so it stays unattributed.
    private string? AuthorName(string? userId) =>
        userId == null ? null : userContext.DisplayName ?? userContext.UserName;

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

    private static QueryVersionDetail ToDetail(QueryVersion version)
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
            CreatedByUserId = version.CreatedByUserId,
            CreatedByUserName = version.CreatedByUserName,
            ChangeSource = version.ChangeSource,
            ChangeReason = version.ChangeReason,
            Steps = steps
        };
    }

    private static List<StepDiff> ComputeStepDiffs(List<QueryStepSnapshot> stepsA, List<QueryStepSnapshot> stepsB)
    {
        var diffs = new List<StepDiff>();
        var allStepOrders = stepsA.Select(s => s.StepOrder)
            .Union(stepsB.Select(s => s.StepOrder))
            .OrderBy(o => o)
            .ToList();

        foreach (var order in allStepOrders)
        {
            var stepA = stepsA.FirstOrDefault(s => s.StepOrder == order);
            var stepB = stepsB.FirstOrDefault(s => s.StepOrder == order);

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

                diffs.Add(new StepDiff
                {
                    StepOrder = order,
                    DiffType = (sqlChanged || dsChanged || nameChanged) ? StepDiffType.Modified : StepDiffType.Unchanged,
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

    private sealed class VersionRow
    {
        public int Id { get; init; }

        public int VersionNumber { get; init; }

        public string? Label { get; init; }

        public QueryVersionStatus Status { get; init; }

        public string Name { get; init; } = string.Empty;

        public DateTime CreatedTime { get; init; }

        public string? CreatedByUserId { get; init; }

        public string? CreatedByUserName { get; init; }

        public string? ChangeSource { get; init; }

        public string? ChangeReason { get; init; }

        public string StepsJson { get; init; } = string.Empty;
    }
}
