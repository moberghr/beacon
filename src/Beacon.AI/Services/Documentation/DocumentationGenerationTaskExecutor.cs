using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Beacon.AI.Services.Documentation;

/// <summary>
/// Runs a <see cref="BackgroundTaskKind.DocumentationGeneration"/> task: the regeneration of one project's documentation,
/// as the user who started it. Right before the first LLM call it checks again that the user may still curate the project
/// (a live Admin does, as the access evaluator decides for an owner) and may still query the project's data sources, and
/// skips the task (<see cref="BackgroundTaskFailureCodes.Forbidden"/>) when not. The LLM calls go through the queue.
/// </summary>
internal sealed class DocumentationGenerationTaskExecutor(
    IProjectDocumentationService documentationService,
    IProjectAccessEvaluator projectAccess,
    IExecutionAccessCheck accessCheck,
    IDbContextFactory<BeaconContext> contextFactory) : IBackgroundTaskExecutor
{
    /// <summary>The resource name the access check logs a refused task under.</summary>
    internal const string AccessResource = "documentation generation";

    public BackgroundTaskKind Kind => BackgroundTaskKind.DocumentationGeneration;

    public TimeSpan AbandonAfter => TimeSpan.FromHours(2);

    public async Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskContext task, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        if (!scope.Allows(task.SubjectId, ProjectAction.Curate))
        {
            return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Forbidden);
        }

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var dataSourceIds = await context.ProjectDataSources
                .WhereReadable()
                .Where(x => x.ProjectId == task.SubjectId)
                .Select(x => x.DataSourceId)
                .ToListAsync(cancellationToken);
            if (dataSourceIds.Count > 0
                && !await accessCheck.AllowsAsync(ProjectAction.Query, dataSourceIds, AccessResource, task.TaskId, cancellationToken))
            {
                return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Forbidden);
            }
        }

        await documentationService.GenerateDocumentationAsync(task.SubjectId, task.RequestedByUserId ?? 0, cancellationToken);

        return new BackgroundTaskOutcome();
    }
}
