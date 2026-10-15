using Microsoft.EntityFrameworkCore;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Services;

namespace Beacon.Api;

/// <summary>
/// Warns after the host has started — off the startup thread and best effort: a database that cannot be read is noted,
/// never a failure — about grounding that no project admin curates: the active golden cases that record no promoting user
/// (promoted before Beacon recorded who promoted a case; they keep grounding SQL generation until an Admin reviews them, and
/// the Admin grounding recheck lists the ones today's checks refuse), and, with Beacon user management on, the live projects
/// that learn from feedback while no enabled user is their ProjectAdmin (only an Admin reviews their learned patterns and
/// documentation patches). Counts and project ids only (§1.11).
/// </summary>
internal sealed class GroundingStartupNote(
    ILogger<GroundingStartupNote> logger,
    BeaconConfiguration? configuration = null) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            var lifetime = builder.ApplicationServices.GetService<IHostApplicationLifetime>();
            lifetime?.ApplicationStarted.Register(
                () => _ = Task.Run(() => NoteAsync(builder.ApplicationServices, lifetime.ApplicationStopping)));

            next(builder);
        };
    }

    /// <summary>Both notes, each read on its own, so one that cannot be read leaves the other.</summary>
    internal async Task NoteAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetService<IDbContextFactory<BeaconContext>>();
        if (contextFactory == null)
        {
            return;
        }

        await NoteSafelyAsync("golden cases without a recorded promoter", () => NoteUnattributedGoldenCasesAsync(contextFactory, cancellationToken));

        var settings = services.GetService<IMcpSettingsProvider>();
        if (settings != null && configuration?.UserManagement.Enabled == true)
        {
            await NoteSafelyAsync("learning projects without a project admin", () => NoteProjectsWithoutCuratorAsync(contextFactory, settings, cancellationToken));
        }
    }

    private async Task NoteUnattributedGoldenCasesAsync(IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var projectIds = await context.McpEvalCases
            .Where(x => x.IsActive)
            .Where(x => x.PromotedByUserId == null)
            .Select(x => x.ProjectId)
            .ToListAsync(cancellationToken);
        if (projectIds.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "{CaseCount} active golden cases record no promoting user (projects {ProjectIds}): they were promoted before Beacon recorded who promoted a case, and they keep grounding SQL generation. Review them on the MCP learning page (Golden examples), where the grounding recheck (GET /beacon/api/eval/grounding/recheck?projectId=) lists the ones today's checks refuse; deactivate the ones nobody stands behind",
            projectIds.Count,
            string.Join(",", projectIds.Distinct().Order()));
    }

    private async Task NoteProjectsWithoutCuratorAsync(
        IDbContextFactory<BeaconContext> contextFactory,
        IMcpSettingsProvider settings,
        CancellationToken cancellationToken)
    {
        if (!(await settings.GetSettingsAsync(cancellationToken)).EnableLearning)
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var withoutCurator = await context.Projects
            .Where(x => !context.ProjectMembers
                .Where(y => y.ProjectId == x.Id)
                .Where(y => y.Role == ProjectRole.ProjectAdmin)
                .Any(y => context.Users
                    .Where(z => z.Id == y.UserId)
                    .Any(z => z.IsEnabled)))
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var learning = new List<int>();
        foreach (var projectId in withoutCurator)
        {
            if ((await settings.GetEffectiveSettingsAsync(projectId, cancellationToken)).EnableLearning)
            {
                learning.Add(projectId);
            }
        }

        if (learning.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "{ProjectCount} live projects learn from MCP feedback and have no enabled ProjectAdmin member (projects {ProjectIds}): only an Admin reviews their learned patterns and documentation patches. Make a project member its ProjectAdmin to curate them",
            learning.Count,
            string.Join(",", learning));
    }

    private async Task NoteSafelyAsync(string what, Func<Task> note)
    {
        try
        {
            await note();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("The {What} could not be counted at startup ({ExceptionType})", what, ex.GetType().Name);
        }
    }
}
