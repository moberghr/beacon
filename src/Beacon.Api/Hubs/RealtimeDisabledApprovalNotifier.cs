using Beacon.Core.Services;

namespace Beacon.Api.Hubs;

/// <summary>
/// The <see cref="IApprovalNotifier"/> that is registered when a host sets
/// <c>BeaconApiOptions.Realtime = false</c>. It is NOT an error path and NOT a fallback for a
/// failed push: with realtime off there is no hub to push to, so dropping the notification is
/// the configured, intended outcome. The type is named for exactly that, so a DI dump or a log
/// line names the configuration rather than looking like a lost message.
/// </summary>
public sealed class RealtimeDisabledApprovalNotifier(ILogger<RealtimeDisabledApprovalNotifier> logger) : IApprovalNotifier
{
    private static int _logged;

    public Task ApprovalUpdatedAsync(
        int approvalId,
        string status,
        string? reviewerUserId,
        string? requesterUserId,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _logged, 1) == 0)
        {
            logger.LogDebug(
                "Realtime is disabled (BeaconApiOptions.Realtime = false); approval status changes are not pushed to clients. Logged once per process.");
        }

        return Task.CompletedTask;
    }
}
