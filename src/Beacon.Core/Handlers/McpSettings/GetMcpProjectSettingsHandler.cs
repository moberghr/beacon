using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class GetMcpProjectSettingsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider)
    : IRequestHandler<GetMcpProjectSettingsQuery, GetMcpProjectSettingsResult>
{
    public async Task<GetMcpProjectSettingsResult> Handle(GetMcpProjectSettingsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var projectExists = await context.Projects
            .Where(x => x.Id == request.ProjectId)
            .AnyAsync(cancellationToken);

        if (!projectExists)
        {
            throw new InvalidOperationException($"Project {request.ProjectId} not found.");
        }

        var overrides = await settingsProvider.GetProjectOverridesAsync(request.ProjectId, cancellationToken) ?? new McpProjectSettingsData();
        var detail = await settingsProvider.GetEffectiveSettingsDetailAsync(request.ProjectId, cancellationToken);

        return new GetMcpProjectSettingsResult(
            request.ProjectId,
            overrides,
            detail.Effective,
            detail.LockedFields.Order().ToArray(),
            detail.ClampedFields.Order().ToArray());
    }
}

public record GetMcpProjectSettingsQuery(int ProjectId) : IRequest<GetMcpProjectSettingsResult>;

/// <param name="Overrides">Stored per-project values; a null member means "inherit".</param>
/// <param name="Effective">What every MCP consumer reads for this project after locks, overrides, global and defaults.</param>
/// <param name="LockedFields">Fields a deployment lock pins — the UI hides them.</param>
/// <param name="ClampedFields">Fields a deployment ceiling lowered — the UI annotates them.</param>
public record GetMcpProjectSettingsResult(
    int ProjectId,
    McpProjectSettingsData Overrides,
    McpSettingsData Effective,
    string[] LockedFields,
    string[] ClampedFields);
