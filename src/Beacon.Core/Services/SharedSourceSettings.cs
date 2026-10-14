using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Models;

namespace Beacon.Core.Services;

/// <summary>
/// The MCP settings for work on data sources that several live projects may link. Each guardrail (read-only
/// enforcement, PII detection and the custom PII patterns, the row, timeout, size and plan-cost limits, value grounding
/// and its probe budget, sample-value collection) is tightened to the strictest value among those projects, so the most
/// protective project's settings follow its data wherever the data is reachable. Every other setting is the asking
/// project's own. Computed here from <see cref="IMcpSettingsProvider"/>'s per-project
/// settings and the stored links, so a host's own settings provider takes part without implementing anything. Registered
/// with <c>TryAdd</c>.
/// </summary>
public interface ISharedSourceSettings
{
    /// <summary>
    /// <paramref name="projectId"/>'s effective settings (<see cref="IMcpSettingsProvider.GetEffectiveSettingsAsync"/>),
    /// tightened to the strictest of the live projects that link any of <paramref name="dataSourceIds"/>.
    /// </summary>
    Task<McpSettingsData> GetEffectiveSettingsAsync(int projectId, IReadOnlyCollection<int> dataSourceIds, CancellationToken ct = default);

    /// <summary>
    /// For work on a data source no project asks for (sampling its values when its metadata is refreshed, showing the
    /// samples stored, the PII settings of a subscription or data-quality run): the global settings, tightened to the
    /// strictest of the live projects that link it. Sample-value collection is the linking projects' own choice: on only
    /// while every live project that links the source has it on in its effective settings (its override, else the global
    /// value), whatever the global value says; the global value decides only for a source no live project links. A
    /// deployment lock (<c>Beacon:Mcp:ForbidRowDataToLlm</c>) still pins it off.
    /// </summary>
    Task<McpSettingsData> GetDataSourceSettingsAsync(int dataSourceId, CancellationToken ct = default);
}

internal sealed class SharedSourceSettings(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider) : ISharedSourceSettings
{
    public async Task<McpSettingsData> GetEffectiveSettingsAsync(int projectId, IReadOnlyCollection<int> dataSourceIds, CancellationToken ct = default)
    {
        var effective = await settingsProvider.GetEffectiveSettingsAsync(projectId, ct);
        if (dataSourceIds.Count == 0)
        {
            return effective;
        }

        var linkingProjectIds = await LinkingProjectIdsAsync(dataSourceIds, ct);

        return await TightenAsync(effective, linkingProjectIds.Where(x => x != projectId), ct);
    }

    public async Task<McpSettingsData> GetDataSourceSettingsAsync(int dataSourceId, CancellationToken ct = default)
    {
        var global = await settingsProvider.GetSettingsAsync(ct);
        var linkingProjectIds = await LinkingProjectIdsAsync([dataSourceId], ct);
        if (linkingProjectIds.Count == 0)
        {
            return global;
        }

        // A project that turns collection on collects although the global value (off by default) does not; any linking
        // project that has it off keeps it off. Every other guardrail stays at least as strict as the global settings.
        var settings = global.Clone();
        var collects = true;
        foreach (var projectId in linkingProjectIds)
        {
            var project = await settingsProvider.GetEffectiveSettingsAsync(projectId, ct);
            McpGuardrails.Tighten(settings, project);
            collects = collects && project.EnableSampleValueCollection;
        }

        settings.EnableSampleValueCollection = collects;

        return settings;
    }

    // Not cached: links change without the settings changing. Each project's own settings come from the provider's cache.
    // Live projects only, through the global soft-delete filter: an explicit `ArchivedTime == null` next to it collapses
    // the predicate to WHERE FALSE, and no project would ever tighten another.
    private async Task<List<int>> LinkingProjectIdsAsync(IReadOnlyCollection<int> dataSourceIds, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Projects
            .Where(x => x.DataSources.Any(y => dataSourceIds.Contains(y.DataSourceId)))
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);
    }

    private async Task<McpSettingsData> TightenAsync(McpSettingsData settings, IEnumerable<int> projectIds, CancellationToken ct)
    {
        McpSettingsData? tightened = null;
        foreach (var projectId in projectIds)
        {
            tightened ??= settings.Clone();
            McpGuardrails.Tighten(tightened, await settingsProvider.GetEffectiveSettingsAsync(projectId, ct));
        }

        return tightened ?? settings;
    }
}
