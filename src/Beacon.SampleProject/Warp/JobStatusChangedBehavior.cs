using Beacon.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Warp.Core.Handlers;

namespace Beacon.SampleProject.Warp;

/// <summary>
/// Warp pipeline behavior that replaces the old <c>HangfireSignalRJobFilter</c>: it publishes
/// <see cref="BeaconHubEventNames.JobStatusChanged"/> events to the user who enqueued a job. The
/// job's <c>BeaconUserId</c> metadata scopes the publish; jobs without it publish nothing — no
/// broadcast to all users (PII guard, §1.11).
///
/// The behavior wraps handler execution, so it covers the <c>Processing → Succeeded/Failed</c>
/// transitions a background job goes through while running (the enqueue itself already returned
/// the job id to the caller). Faults in delivery are logged, never allowed to fail the job.
/// </summary>
public sealed class JobStatusChangedBehavior<TRequest, TResponse>(
    IJobContext jobContext,
    IHubContext<BeaconHub> hubContext,
    TimeProvider timeProvider,
    ILogger<JobStatusChangedBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        var userId = TryGetBeaconUserId();
        if (userId == null)
        {
            return await next(request, cancellationToken);
        }

        Publish(userId, "Processing");
        try
        {
            var response = await next(request, cancellationToken);
            Publish(userId, "Succeeded");

            return response;
        }
        catch
        {
            Publish(userId, "Failed");
            throw;
        }
    }

    private string? TryGetBeaconUserId()
    {
        if (jobContext.Metadata.TryGetValue("BeaconUserId", out var value)
            && value is string userId
            && !string.IsNullOrWhiteSpace(userId))
        {
            return userId;
        }

        return null;
    }

    private void Publish(string userId, string state)
    {
        var payload = new JobStatusChangedEvent(jobContext.JobId.ToString(), state, timeProvider.GetUtcNow());
        var jobId = jobContext.JobId;

        // Fire-and-forget — the job outcome must not hinge on SignalR delivery, but faults are
        // observed and logged instead of being silently swallowed.
        _ = hubContext.Clients.User(userId).SendAsync(BeaconHubEventNames.JobStatusChanged, payload, CancellationToken.None)
            .ContinueWith(
                x => logger.LogWarning(x.Exception, "Failed to publish JobStatusChanged for job {JobId}.", jobId),
                TaskContinuationOptions.OnlyOnFaulted);
    }
}
