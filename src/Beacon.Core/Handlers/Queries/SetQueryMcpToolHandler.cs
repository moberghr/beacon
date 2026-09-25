using Beacon.Core.Data;
using Beacon.Core.SavedQueries;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Queries;

/// <summary>
/// Exposes a saved query as the MCP tool <c>q_&lt;name&gt;</c>, or withdraws it. The name must match
/// <c>^[a-z][a-z0-9_]{2,40}$</c> and is unique across live queries (so it is unique in every project the query is
/// visible in, and names one query for a caller who reaches several projects). Enabling requires a runnable approved
/// version (<see cref="SavedQueryRunnableVersion"/>) whose parameters make a valid tool.
/// </summary>
internal sealed class SetQueryMcpToolHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ILogger<SetQueryMcpToolHandler> logger)
    : IRequestHandler<SetQueryMcpToolCommand, SetQueryMcpToolResult>
{
    public async Task<SetQueryMcpToolResult> Handle(SetQueryMcpToolCommand request, CancellationToken cancellationToken)
    {
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

            if (takenBy != null)
            {
                throw new InvalidOperationException($"The tool name '{name}' is already used by query #{takenBy}.");
            }
        }

        var runnable = await SavedQueryRunnableVersion.ForQuery(context, request.QueryId)
            .FirstOrDefaultAsync(cancellationToken);
        var issue = runnable == null
            ? SavedQueryRunnableVersion.NoRunnableVersionIssue
            : SavedQueryToolRules.Inspect(runnable.StepsJson).Issue;

        if (request.Enabled && issue != null)
        {
            throw new InvalidOperationException($"The query cannot be exposed as an MCP tool: {issue}");
        }

        query.McpToolEnabled = request.Enabled;
        query.McpToolName = name;
        query.McpToolDescription = description;

        await context.SaveChangesAsync(cancellationToken);

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
}

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
