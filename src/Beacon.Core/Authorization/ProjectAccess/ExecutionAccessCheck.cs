using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The default <see cref="IExecutionAccessCheck"/>: the evaluator's scope for the action, applied to the data sources
/// with <see cref="ProjectSourceFootprint"/>. A scope that reaches every project decides without a database read.
/// </summary>
internal sealed class ExecutionAccessCheck(
    IProjectAccessEvaluator evaluator,
    IBeaconActorAccessor actorAccessor,
    IRunAsScope runAs,
    IDbContextFactory<BeaconContext> contextFactory,
    ILogger<ExecutionAccessCheck> logger) : IExecutionAccessCheck
{
    public async Task<bool> AllowsAsync(
        ProjectAction action,
        IReadOnlyCollection<int> dataSourceIds,
        string resource,
        int resourceId,
        CancellationToken cancellationToken)
    {
        var scope = await evaluator.GetScopeAsync(cancellationToken);
        var allowed = scope.For(action);
        if (allowed.Ids == null)
        {
            return true;
        }

        if (await HoldsAsync(allowed, dataSourceIds, cancellationToken))
        {
            return true;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (runAs.IsSet)
        {
            AccessRefusal.Skipped(logger, actor, resource, resourceId, action);
        }
        else
        {
            _ = AccessRefusal.Of(logger, actor, $"{resource} ({action})", resourceId, $"{action} is not allowed on this {resource}'s data sources.");
        }

        return false;
    }

    private async Task<bool> HoldsAsync(ProjectSet allowed, IReadOnlyCollection<int> dataSourceIds, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await ProjectSourceFootprint.AllowsAsync(context, allowed, dataSourceIds, cancellationToken);
    }
}
