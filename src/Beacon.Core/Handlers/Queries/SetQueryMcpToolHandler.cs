using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.SavedQueries;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Queries;

/// <summary>
/// Exposes a saved query as the MCP tool <c>q_&lt;name&gt;</c>, or withdraws it. The name must match
/// <c>^[a-z][a-z0-9_]{2,40}$</c> and is unique across live queries (so it is unique in every project the query is
/// visible in, and names one query for a caller who reaches several projects). Enabling requires a runnable approved
/// version (<see cref="SavedQueryRunnableVersion"/>) whose parameters make a valid tool. Anyone but an Admin publishes
/// only a query it can read (404 otherwise): the SecurityAdmin permission grants no project data. While the approval
/// workflow is on, publishing is a third pair of eyes: whoever requested or approved the active version may not enable
/// its tool, nor may a caller without a user id, who could be either (403), unless the host allows self-approval
/// (<see cref="ApprovalWorkflowOptions.AllowSelfApproval"/>). The check is made when the tool is enabled: a version
/// approved later is served without it. With the workflow off nobody approves a change, and the check does not apply.
/// Withdrawing a tool is always allowed.
/// </summary>
internal sealed class SetQueryMcpToolHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ProjectRequestAuthorizer authorizer,
    IBeaconActorAccessor actorAccessor,
    BeaconConfiguration configuration,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider,
    ILogger<SetQueryMcpToolHandler> logger)
    : IRequestHandler<SetQueryMcpToolCommand, SetQueryMcpToolResult>
{
    public async Task<SetQueryMcpToolResult> Handle(SetQueryMcpToolCommand request, CancellationToken cancellationToken)
    {
        await authorizer.DemandAsync(new ProjectCheck(ProjectResource.Query(request.QueryId), ProjectAction.Read), cancellationToken);

        var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        if (request.Enabled && name == null)
        {
            throw new InvalidOperationException("A tool name is required to enable the MCP tool.");
        }

        if (name != null && !SavedQueryToolRules.IsValidName(name))
        {
            throw new InvalidOperationException("The tool name must be 3–41 characters of lowercase letters, digits and underscores, starting with a letter.");
        }

        if (description?.Length > SavedQueryToolRules.MaxDescriptionLength)
        {
            throw new InvalidOperationException($"The tool description must be at most {SavedQueryToolRules.MaxDescriptionLength} characters.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = await context.Queries
            .Where(x => x.Id == request.QueryId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query #{request.QueryId} not found.");

        if (name != null)
        {
            var takenBy = await BuildNameTakenQuery(context, name, request.QueryId)
                .FirstOrDefaultAsync(cancellationToken);

            if (takenBy != null && request.Enabled)
            {
                throw new InvalidOperationException($"The tool name '{name}' is already used by query #{takenBy}.");
            }

            if (takenBy != null)
            {
                // Disabling must always work: keep the name this query already has instead of taking a clashing one.
                logger.LogInformation("Query {QueryId} MCP tool disabled; requested name is used by query {OtherQueryId}, kept the stored name.", query.Id, takenBy);
                name = query.McpToolName;
            }
        }

        var runnable = await SavedQueryRunnableVersion.ForQuery(context, request.QueryId, SavedQueryRunnableVersion.RequiresApprovedHash(configuration))
            .FirstOrDefaultAsync(cancellationToken);
        var issue = runnable == null
            ? SavedQueryRunnableVersion.NoRunnableVersionIssue
            : SavedQueryToolRules.Inspect(runnable.StepsJson).Issue;

        if (request.Enabled && issue != null)
        {
            throw new InvalidOperationException($"The query cannot be exposed as an MCP tool: {issue}");
        }

        // Only while the workflow is on: with it off nobody approves a change, so there is no second pair of eyes to keep.
        if (request.Enabled && runnable != null && configuration.ApprovalWorkflow is { Enabled: true, AllowSelfApproval: false })
        {
            await EnsureNotRequesterOrApproverAsync(context, request.QueryId, runnable.VersionId, cancellationToken);
        }

        // Reported, not blocking: the data sources may be added to a project later, and the tool shows up then.
        issue ??= await SavedQueryRunnableVersion.InspectIssueAsync(context, runnable, cancellationToken);

        var wasEnabled = query.McpToolEnabled;
        query.McpToolEnabled = request.Enabled;
        query.McpToolName = name;
        query.McpToolDescription = description;
        query.RecordChangeBy(await actorAccessor.GetCurrentAsync(cancellationToken), timeProvider);
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.QueryMcpToolChanged)
                .On(SecurityAuditResources.Query, query.Id)
                .With(new SecurityAuditDetails()
                    .Add("enabled", request.Enabled)
                    .Add("wasEnabled", wasEnabled)
                    .AddCode("tool", name == null ? null : SavedQueryToolRules.ToolName(name))
                    .Add("versionNumber", runnable?.VersionNumber)),
            cancellationToken);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            // A concurrent save took the name between the check above and this write (the index is the arbiter).
            throw new InvalidOperationException($"The tool name '{name}' is already used by another query.", ex);
        }

        logger.LogInformation(
            "Query {QueryId} MCP tool {Enabled} as {ToolName} by user {UserId}",
            query.Id,
            request.Enabled ? "enabled" : "disabled",
            name,
            request.UserId);

        return new SetQueryMcpToolResult(
            query.Id,
            query.McpToolEnabled,
            query.McpToolName,
            query.McpToolDescription,
            name == null ? null : SavedQueryToolRules.ToolName(name),
            runnable?.VersionNumber,
            issue);
    }

    internal static IQueryable<int?> BuildNameTakenQuery(BeaconContext context, string name, int queryId)
    {
        return context.Queries
            .Where(x => x.McpToolName == name)
            .Where(x => x.Id != queryId)
            .Select(x => (int?)x.Id);
    }

    // The approvals of the version the tool would run name who asked for it and who approved it; neither publishes it. A
    // caller Beacon cannot name could be either, so it publishes nothing.
    private async Task EnsureNotRequesterOrApproverAsync(BeaconContext context, int queryId, int versionId, CancellationToken cancellationToken)
    {
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (caller.UserId == null)
        {
            throw AccessRefusal.Of(
                logger,
                caller,
                "query",
                queryId,
                "A query is published as an MCP tool by a signed-in user who neither requested nor approved its active version.");
        }

        // Who requested and who approved each approved request for the version.
        var approvals = await context.QueryApprovalRequests
            .Where(x => x.QueryVersionId == versionId)
            .Where(x => x.Status == ApprovalStatus.Approved)
            .Select(x =>
                new
                {
                    x.RequestedByUserId,
                    x.ReviewedByUserId
                })
            .ToListAsync(cancellationToken);

        if (approvals.Any(x => caller.IsRecordedAs(x.RequestedByUserId) || caller.IsRecordedAs(x.ReviewedByUserId)))
        {
            throw AccessRefusal.Of(
                logger,
                caller,
                "query",
                queryId,
                "A query is published as an MCP tool by someone other than who requested or approved its active version.");
        }
    }
}

[ProjectScopeExempt("gated by the SecurityAdmin permission: publishing a saved query as an MCP tool is a publishing decision; the handler demands Read on the query of anyone but an Admin", Policy = BeaconPermissions.SecurityAdminPolicy)]
public record SetQueryMcpToolCommand(int QueryId, bool Enabled, string? Name, string? Description, string? UserId)
    : IRequest<SetQueryMcpToolResult>;

/// <param name="ToolName">The MCP tool name (<c>q_&lt;name&gt;</c>), or null when no name is set.</param>
/// <param name="RunnableVersionNumber">The approved version the tool runs, or null when there is none.</param>
/// <param name="Issue">Why the query cannot be a tool right now, or null when it can.</param>
public record SetQueryMcpToolResult(
    int QueryId,
    bool Enabled,
    string? Name,
    string? Description,
    string? ToolName,
    int? RunnableVersionNumber,
    string? Issue);
