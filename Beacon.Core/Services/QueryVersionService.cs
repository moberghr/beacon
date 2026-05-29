using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

public interface IQueryVersionService
{
    Task<QueryVersion> CreateVersionAsync(int queryId, string? userId, string? source, string? reason, QueryVersionStatus status, CancellationToken cancellationToken = default);
    Task<List<QueryVersionSummary>> GetVersionsAsync(int queryId, CancellationToken cancellationToken = default);
    Task<QueryVersionDetail?> GetVersionDetailAsync(int versionId, CancellationToken cancellationToken = default);
    Task<int> RestoreVersionAsync(int versionId, string? userId, CancellationToken cancellationToken = default);
    Task<QueryVersionDiff> DiffVersionsAsync(int versionIdA, int versionIdB, CancellationToken cancellationToken = default);
}

internal class QueryVersionService(IDbContextFactory<BeaconContext> contextFactory, ILogger<QueryVersionService> logger) : IQueryVersionService
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

        var snapshots = query.Steps.OrderBy(s => s.StepOrder).Select(s => new QueryStepSnapshot
        {
            StepOrder = s.StepOrder,
            SqlValue = s.SqlValue,
            DataSourceId = s.DataSourceId,
            DataSourceName = s.DataSource.Name,
            Name = s.Name,
            Description = s.Description,
            Parameters = s.Parameters.Select(p => new QueryStepParameterSnapshot
            {
                Name = p.Name,
                Type = p.Type,
                Description = p.Description,
                Placeholder = p.Placeholder
            }).ToList()
        }).ToList();

        var version = new QueryVersion
        {
            QueryId = queryId,
            VersionNumber = maxVersion + 1,
            Status = status,
            Name = query.Name,
            Description = query.Description,
            FinalQuery = query.FinalQuery,
            StepsJson = JsonSerializer.Serialize(snapshots, JsonOptions),
            CreatedByUserId = userId,
            ChangeSource = source,
            ChangeReason = reason
        };

        context.QueryVersions.Add(version);

        // If this is the active version, point the query at it via navigation
        // so EF resolves the FK during the single SaveChanges below (§5.7).
        if (status == QueryVersionStatus.Active)
        {
            query.ActiveVersion = version;
        }

        await context.SaveChangesAsync(cancellationToken);
        return version;
    }

    public async Task<List<QueryVersionSummary>> GetVersionsAsync(int queryId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var versions = await context.QueryVersions
            .Where(v => v.QueryId == queryId)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new
            {
                v.Id,
                v.VersionNumber,
                v.Label,
                v.Status,
                v.Name,
                v.CreatedTime,
                v.CreatedByUserId,
                v.ChangeSource,
                v.ChangeReason,
                v.StepsJson
            })
            .ToListAsync(cancellationToken);

        return versions.Select(v => new QueryVersionSummary
        {
            Id = v.Id,
            VersionNumber = v.VersionNumber,
            Label = v.Label,
            Status = v.Status,
            Name = v.Name,
            CreatedTime = v.CreatedTime,
            CreatedByUserId = v.CreatedByUserId,
            ChangeSource = v.ChangeSource,
            ChangeReason = v.ChangeReason,
            StepCount = CountSteps(v.StepsJson)
        }).ToList();
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

        // Archive the current active version
        var currentActive = await context.QueryVersions
            .Where(v => v.QueryId == query.Id)
            .Where(v => v.Status == QueryVersionStatus.Active)
            .FirstOrDefaultAsync(cancellationToken);

        if (currentActive != null)
        {
            currentActive.Status = QueryVersionStatus.Archived;
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
}
