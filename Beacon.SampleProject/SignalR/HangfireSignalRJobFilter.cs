using Beacon.Api.Hubs;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.AspNetCore.SignalR;

namespace Beacon.SampleProject.SignalR;

/// <summary>
/// Hangfire filter that publishes <see cref="BeaconHubEventNames.JobStatusChanged"/> events
/// to the user who enqueued the job. The job's <c>BeaconUserId</c> parameter scopes the publish.
/// Jobs without that parameter publish nothing — no broadcast to all users (PII risk per §1.11).
/// Jobs that want push events should set the parameter at enqueue time:
///   <c>BackgroundJob.Enqueue(... );  // Hangfire sets it via state filter on enqueue</c>
/// or via <c>BackgroundJobClient.SetJobParameter(jobId, "BeaconUserId", userId)</c>.
/// </summary>
public sealed class HangfireSignalRJobFilter : JobFilterAttribute, IApplyStateFilter
{
    private readonly IServiceProvider _serviceProvider;

    public HangfireSignalRJobFilter(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        var userId = context.Connection.GetJobParameter(context.BackgroundJob.Id, "BeaconUserId");
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var hubContext = _serviceProvider.GetService(typeof(IHubContext<BeaconHub>)) as IHubContext<BeaconHub>;
        if (hubContext is null)
        {
            return;
        }

        var payload = new JobStatusChangedEvent(
            context.BackgroundJob.Id,
            context.NewState.Name,
            DateTimeOffset.UtcNow);

        // Fire-and-forget — Hangfire transaction commits regardless of SignalR delivery,
        // but faults are observed and logged instead of being silently swallowed.
        var logger = _serviceProvider.GetService(typeof(ILogger<HangfireSignalRJobFilter>)) as ILogger<HangfireSignalRJobFilter>;
        var jobId = context.BackgroundJob.Id;
        _ = hubContext.Clients.User(userId).SendAsync(BeaconHubEventNames.JobStatusChanged, payload)
            .ContinueWith(
                x => logger?.LogWarning(x.Exception, "Failed to publish JobStatusChanged for job {JobId}.", jobId),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
