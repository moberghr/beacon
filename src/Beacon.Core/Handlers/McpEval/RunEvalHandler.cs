using MediatR;
using Beacon.Core.Services;
using Beacon.Core.Worker;

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
    public async Task<RunEvalResult> Handle(RunEvalCommand request, CancellationToken cancellationToken)
    {
        var runId = await evalService.StartRunAsync(request.ProjectId, request.TriggeredByUserId, cancellationToken);
        await scheduler.EnqueueMcpEval(runId);

        return new RunEvalResult(runId);
    }
}

public record RunEvalCommand(int? ProjectId, int? TriggeredByUserId) : IRequest<RunEvalResult>;

public record RunEvalResult(int RunId);
