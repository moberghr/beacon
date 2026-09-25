using Beacon.Core.Data;
using Beacon.Core.Models.Queries;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.SavedQueries;

/// <summary>A saved query exposed as an MCP tool, resolved to the approved version it runs.</summary>
/// <param name="McpToolName">The admin-chosen name; the MCP tool is <see cref="ToolName"/>.</param>
/// <param name="DataSourceIds">Every data source the version's steps read.</param>
/// <param name="ProjectIds">
/// The requested projects that contain ALL of <see cref="DataSourceIds"/> — the projects the tool is visible in.
/// </param>
public sealed record SavedQueryToolDefinition(
    int QueryId,
    string McpToolName,
    string Title,
    string Description,
    int QueryVersionId,
    int VersionNumber,
    IReadOnlyList<QueryStepSnapshot> Steps,
    string? FinalQuery,
    IReadOnlyList<SavedQueryToolParameter> Parameters,
    IReadOnlyList<int> DataSourceIds,
    IReadOnlyList<int> ProjectIds)
{
    public string ToolName => SavedQueryToolRules.ToolName(McpToolName);
}

/// <summary>
/// Loads the callable saved-query tools for a set of projects. Visibility is authorization (§1.12): a tool is returned
/// for a project only when that project contains every data source the approved version reads, so a query over a data
/// source shared by two projects shows in both only when each has all of its sources, and never in a project the
/// caller did not pass.
/// </summary>
public interface ISavedQueryToolSource
{
    /// <summary>
    /// Enabled tools with a runnable approved version (see <see cref="SavedQueryRunnableVersion"/>) visible in at
    /// least one of <paramref name="projectIds"/>, ordered by tool name. An empty set returns nothing (fail closed).
    /// </summary>
    Task<IReadOnlyList<SavedQueryToolDefinition>> GetToolsAsync(IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken);
}

internal sealed record SavedQueryToolRow(
    int QueryId,
    string McpToolName,
    string? McpToolDescription,
    int QueryVersionId,
    int VersionNumber,
    string Name,
    string? Description,
    string StepsJson,
    string? FinalQuery);

internal sealed class SavedQueryToolSource(IDbContextFactory<BeaconContext> contextFactory) : ISavedQueryToolSource
{
    public async Task<IReadOnlyList<SavedQueryToolDefinition>> GetToolsAsync(IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await BuildEnabledToolsQuery(context)
            .ToListAsync(cancellationToken);

        var candidates = rows
            .Select(x => (Row: x, Shape: SavedQueryToolRules.Inspect(x.StepsJson)))
            .Where(x => x.Shape.Issue == null)
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var dataSourceIds = candidates
            .SelectMany(x => x.Shape.Steps)
            .Select(x => x.DataSourceId)
            .Distinct()
            .ToList();
        var requestedProjectIds = projectIds.ToList();

        var memberships = await BuildMembershipQuery(context, requestedProjectIds, dataSourceIds)
            .ToListAsync(cancellationToken);

        var dataSourcesByProject = memberships
            .GroupBy(x => x.ProjectId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.DataSourceId).ToHashSet());

        var tools = new List<SavedQueryToolDefinition>();
        foreach (var (row, shape) in candidates)
        {
            var toolDataSourceIds = shape.Steps
                .Select(x => x.DataSourceId)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var visibleIn = dataSourcesByProject
                .Where(x => toolDataSourceIds.All(x.Value.Contains))
                .Select(x => x.Key)
                .OrderBy(x => x)
                .ToList();

            if (visibleIn.Count == 0)
            {
                continue;
            }

            tools.Add(new SavedQueryToolDefinition(
                row.QueryId,
                row.McpToolName,
                row.Name,
                FirstNonBlank(row.McpToolDescription, row.Description) ?? row.Name,
                row.QueryVersionId,
                row.VersionNumber,
                shape.Steps,
                string.IsNullOrWhiteSpace(row.FinalQuery) ? null : row.FinalQuery,
                shape.Parameters,
                toolDataSourceIds,
                visibleIn));
        }

        return tools;
    }

    internal static IQueryable<SavedQueryToolRow> BuildEnabledToolsQuery(BeaconContext context)
    {
        return context.Queries
            .Where(x => x.McpToolEnabled)
            .Where(x => x.McpToolName != null)
            .WhereHasRunnableVersion(context)
            .OrderBy(x => x.McpToolName)
            .Select(x =>
                new SavedQueryToolRow(
                    x.Id,
                    x.McpToolName!,
                    x.McpToolDescription,
                    x.ActiveVersion!.Id,
                    x.ActiveVersion.VersionNumber,
                    x.ActiveVersion.Name,
                    x.ActiveVersion.Description,
                    x.ActiveVersion.StepsJson,
                    x.ActiveVersion.FinalQuery));
    }

    internal static IQueryable<ProjectDataSourceMembership> BuildMembershipQuery(BeaconContext context, List<int> projectIds, List<int> dataSourceIds)
    {
        return context.ProjectDataSources
            .Where(x => projectIds.Contains(x.ProjectId))
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .Where(x => x.DataSource.ArchivedTime == null)
            .Select(x =>
                new ProjectDataSourceMembership(x.ProjectId, x.DataSourceId));
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .FirstOrDefault();
}

internal sealed record ProjectDataSourceMembership(int ProjectId, int DataSourceId);
