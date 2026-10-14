using MediatR;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Starts an eval run and enqueues its execution as fire-and-forget background work. Creates the run
/// row synchronously (so the caller gets an id immediately) via <see cref="IMcpEvalService.StartRunAsync"/>,
/// then hands the actual case-by-case execution to the host's job runner (Hangfire) through
/// <see cref="IBeaconScheduler.EnqueueMcpEval"/>, which enqueues <c>IJobService.RunMcpEval(runId)</c>.
/// </summary>
internal sealed class RunEvalHandler(IMcpEvalService evalService, IBeaconScheduler scheduler)
    : IRequestHandler<RunEvalCommand, RunEvalResult>
{
    /// <summary>The 400 for a run without a project: golden cases are project-scoped, so a run covers one project.</summary>
    public const string ProjectRequiredMessage = "An eval run needs a project.";

    public async Task<RunEvalResult> Handle(RunEvalCommand request, CancellationToken cancellationToken)
    {
        // Refused before a run is created: a run without a project would never evaluate a case.
        if (request.ProjectId == null)
        {
            throw new InvalidOperationException(ProjectRequiredMessage);
        }

        var runId = await evalService.StartRunAsync(request.ProjectId, request.TriggeredByUserId, cancellationToken);
        await scheduler.EnqueueMcpEval(runId);

        return new RunEvalResult(runId);
    }
}

/// <summary>Starts an eval run over one project's golden cases; <c>ProjectId</c> is required (400 without one).</summary>
[ProjectScopeExempt("admin-gated curation (the eval harness)", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record RunEvalCommand(int? ProjectId, int? TriggeredByUserId) : IRequest<RunEvalResult>;

public record RunEvalResult(int RunId);
