using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;
using Beacon.Core.SavedQueries;

namespace Beacon.Core.Services;

/// <summary>
/// What the approval workflow's startup note counts, read once at startup for the log (counts and tool names only):
/// queries whose stored SQL no version records, queries with subscriptions whose stored SQL is not their active version's
/// content, queries with subscriptions or MCP tools whose active version is not approved for its content hash, the active
/// users who can approve a change through their stored roles, and the MCP tools whose approval recorded no content hash.
/// </summary>
internal static class ApprovalWorkflowReport
{
    /// <summary>The most tool names the note lists; the count covers the rest.</summary>
    public const int MaxListedTools = 20;

    /// <summary>
    /// Live queries with steps and no active version: stored before the workflow was on and never versioned since, they
    /// run as stored outside strict mode, and not at all in it.
    /// </summary>
    public static async Task<int> CountUnversionedQueriesAsync(IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Queries
            .Where(x => x.ActiveVersionId == null)
            .Where(x => x.Steps.Any())
            .CountAsync(cancellationToken);
    }

    /// <summary>
    /// Queries with a subscription whose stored SQL is not their active version's content (changed outside a versioned
    /// save, or a version whose snapshot cannot be read): with the workflow on, every run of them is skipped. Compared in
    /// memory, by content hash, once at startup.
    /// </summary>
    public static async Task<int> CountDriftedScheduledQueriesAsync(IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var queries = await context.Queries
            .Where(x => x.ActiveVersionId != null)
            .Where(x => x.Subscriptions.Any())
            .Select(x =>
                new
                {
                    x.FinalQuery,
                    Steps = x.Steps
                        .Select(y =>
                            new QueryStepData
                            {
                                StepId = y.Id,
                                StepOrder = y.StepOrder,
                                Name = y.Name ?? string.Empty,
                                SqlValue = y.SqlValue,
                                DataSourceId = y.DataSourceId,
                                Parameters = y.Parameters
                                    .Select(z =>
                                        new QueryStepParameterData
                                        {
                                            Name = z.Name,
                                            Type = z.Type,
                                            Placeholder = z.Placeholder
                                        })
                                    .ToList()
                            })
                        .ToList(),
                    x.ActiveVersion!.ContentHash,
                    StepsJson = x.ActiveVersion.ContentHash == null ? x.ActiveVersion.StepsJson : null,
                    ActiveFinalQuery = x.ActiveVersion.FinalQuery
                })
            .ToListAsync(cancellationToken);

        // The stored steps in run order: by order, then by id, as a run reads them.
        return queries.Count(x => !string.Equals(
            QueryContentHash.OfVersion(x.ContentHash, x.StepsJson ?? string.Empty, x.ActiveFinalQuery),
            QueryContentHash.Of(
                    x.FinalQuery,
                    x.Steps
                        .OrderBy(y => y.StepOrder)
                        .ThenBy(y => y.StepId))
                .Hash,
            StringComparison.Ordinal));
    }

    /// <summary>
    /// Queries with a subscription or an enabled MCP tool whose active version is not approved for its content hash (it
    /// went live while the workflow was off, or was approved before Beacon kept hashes): strict mode runs neither until the
    /// version is approved again.
    /// </summary>
    public static async Task<int> CountActiveVersionsNotApprovedForTheirHashAsync(IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Queries
            .Where(x => x.ActiveVersionId != null)
            .Where(x => x.Subscriptions.Any() || x.McpToolEnabled)
            .Where(x => !context.QueryApprovalRequests
                .Where(y => y.QueryVersionId == x.ActiveVersionId)
                .Where(y => y.Status == ApprovalStatus.Approved)
                .Where(y => y.ApprovedContentHash != null)
                .Any(y => y.ApprovedContentHash == x.ActiveVersion!.ContentHash))
            .CountAsync(cancellationToken);
    }

    /// <summary>
    /// Enabled, live users who hold the Approver permission through their stored roles: a super admin, an Admin, or a
    /// holder of the Approver functional role (unless, with separation of duties on, their functional roles conflict).
    /// Permissions an identity provider maps at sign-in are not stored, so they are not counted.
    /// </summary>
    public static async Task<int> CountApproversAsync(
        IDbContextFactory<BeaconContext> contextFactory,
        RoleMappingOptions mapping,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Functional roles are mapped to permissions as the permission accessor maps them (names compared ignoring case).
        var candidates = await context.Users
            .Where(x => x.IsEnabled)
            .Where(x => x.IsSuperAdmin || x.UserRoles.Any())
            .Select(x =>
                new
                {
                    IsAdmin = x.IsSuperAdmin || x.UserRoles.Any(y => y.Role.Name == RoleService.RoleNames.Admin),
                    FunctionalRoles = x.UserRoles
                        .Where(y => y.Role.IsSystemRole)
                        .Where(y => y.Role.Level == RoleService.RoleLevels.Functional)
                        .Select(y => y.Role.Name)
                        .ToList()
                })
            .ToListAsync(cancellationToken);

        return candidates.Count(x => x.IsAdmin || HoldsApprover(mapping, x.FunctionalRoles));
    }

    /// <summary>
    /// The names of the enabled MCP tools whose active version runs through an approval that recorded no content hash
    /// (approved before Beacon kept hashes), by name, at most <see cref="MaxListedTools"/>; and how many there are.
    /// </summary>
    public static async Task<(int Count, IReadOnlyList<string> Names)> ToolsOnApprovalsWithoutAHashAsync(
        IDbContextFactory<BeaconContext> contextFactory,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var names = await context.Queries
            .WhereToolRunsOnAnApprovalWithoutAHash(context)
            .OrderBy(x => x.McpToolName)
            .Select(x => x.McpToolName!)
            .ToListAsync(cancellationToken);

        return (names.Count, names
            .Take(MaxListedTools)
            .Select(SavedQueryToolRules.ToolName)
            .ToList());
    }

    // Under separation of duties a conflicting pair grants neither of its permissions.
    private static bool HoldsApprover(RoleMappingOptions mapping, IReadOnlyList<string> functionalRoles)
    {
        var permissions = mapping.PermissionsOfRoles(functionalRoles);
        if (!permissions.Contains(BeaconPermissions.Approver))
        {
            return false;
        }

        return !mapping.SeparationOfDuties.Enabled
            || BeaconPermissions.WithoutConflicts(permissions.ToList()).Kept.Contains(BeaconPermissions.Approver);
    }
}
