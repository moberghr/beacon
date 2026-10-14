using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Projects;

/// <summary>
/// The guardrails that apply to each of a project's data sources that other live projects link too: the strictest of
/// those projects' settings (<see cref="McpGuardrails"/>), and the project that sets each. For an Admin's view of the
/// project's access (the project detail), since it names other, possibly restricted, projects.
/// </summary>
internal static class SharedSourceGuardrails
{
    public static async Task<List<SharedSourceGuardrailsEntry>> ReadAsync(
        BeaconContext context,
        IMcpSettingsProvider settingsProvider,
        int projectId,
        CancellationToken cancellationToken)
    {
        var sourceIds = context.ProjectDataSources
            .Where(x => x.ProjectId == projectId)
            .Select(x => x.DataSourceId);

        // Live projects only, through the global soft-delete filter: an explicit `ArchivedTime == null` next to it collapses
        // the predicate to WHERE FALSE.
        var links = await context.Projects
            .SelectMany(x => x.DataSources
                .Where(y => sourceIds.Contains(y.DataSourceId))
                .Select(y =>
                    new
                    {
                        y.DataSourceId,
                        DataSourceName = y.DataSource.Name,
                        ProjectId = x.Id,
                        ProjectName = x.Name
                    }))
            .ToListAsync(cancellationToken);

        var shared = links
            .GroupBy(x => x.DataSourceId)
            .Where(x => x.Any(y => y.ProjectId == projectId))
            .Where(x => x.Any(y => y.ProjectId != projectId))
            .OrderBy(x => x.Key)
            .ToList();
        if (shared.Count == 0)
        {
            return [];
        }

        var projectNames = links
            .GroupBy(x => x.ProjectId)
            .ToDictionary(x => x.Key, x => x.First().ProjectName);
        var settings = new Dictionary<int, McpSettingsData>();
        foreach (var id in projectNames.Keys.Order())
        {
            settings[id] = await settingsProvider.GetEffectiveSettingsAsync(id, cancellationToken);
        }

        var entries = new List<SharedSourceGuardrailsEntry>();
        foreach (var source in shared)
        {
            // The project itself first, then the others by id: it keeps a value it already holds.
            var projects = source
                .Select(x => x.ProjectId)
                .Distinct()
                .OrderBy(x => x == projectId ? 0 : 1)
                .ThenBy(x => x)
                .Select(x => (x, settings[x]))
                .ToList();

            var guardrails = McpGuardrails.Describe(projects)
                .Select(x => new SharedSourceGuardrail(x.Setting, x.Value, x.ProjectId, projectNames[x.ProjectId]))
                .ToList();

            entries.Add(new SharedSourceGuardrailsEntry(source.Key, source.First().DataSourceName, guardrails));
        }

        return entries;
    }
}

/// <summary>
/// A data source of the project that other live projects link too, and the guardrails that apply to it whichever of those
/// projects queries it.
/// </summary>
public record SharedSourceGuardrailsEntry(int DataSourceId, string DataSourceName, List<SharedSourceGuardrail> Guardrails);

/// <summary>
/// One guardrail of a shared data source: <c>Setting</c> is the MCP setting (<c>enforceReadOnly</c>,
/// <c>enablePiiDetection</c>, <c>customPiiPatterns</c>, <c>maxRowLimit</c>, <c>statementTimeoutSeconds</c>,
/// <c>maxResultBytes</c>, <c>maxExplainCost</c>); <c>Value</c> its effective value as text (<c>true</c>/<c>false</c>, a
/// number, <c>none</c> for no plan-cost limit, the number of custom PII patterns); <c>ProjectId</c> and
/// <c>ProjectName</c> the project that sets it — the project itself when it already holds that value.
/// </summary>
public record SharedSourceGuardrail(string Setting, string Value, int ProjectId, string ProjectName);
