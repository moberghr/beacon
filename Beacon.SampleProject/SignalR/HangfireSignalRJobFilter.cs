using Beacon.SampleProject.Hubs;
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
internal sealed class HangfireSignalRJobFilter : JobFilterAttribute, IApplyStateFilter
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

        // Fire-and-forget — Hangfire transaction commits regardless of SignalR delivery.
        _ = hubContext.Clients.User(userId).SendAsync(BeaconHubEventNames.JobStatusChanged, payload);
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
