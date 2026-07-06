using Beacon.Core.Services;
using Microsoft.AspNetCore.SignalR;

namespace Beacon.Api.Hubs;

/// <summary>
/// SignalR-backed <see cref="IApprovalNotifier"/>. Targeted push: notify the reviewer (so
/// other tabs they have open update) and the requester (so they see the approval/rejection
/// live). All other connected clients are intentionally NOT notified — broadcast was overkill.
/// </summary>
public sealed class SignalRApprovalNotifier(IHubContext<BeaconHub> hub) : IApprovalNotifier
{
    public async Task ApprovalUpdatedAsync(
        int approvalId,
        string status,
        string? reviewerUserId,
        string? requesterUserId,
        CancellationToken cancellationToken = default)
    {
        var payload = new ApprovalUpdatedEvent(approvalId, status);
        var recipients = new List<string>(2);
        if (!string.IsNullOrEmpty(reviewerUserId))
        {
            recipients.Add(reviewerUserId);
        }

        if (!string.IsNullOrEmpty(requesterUserId) && requesterUserId != reviewerUserId)
        {
            recipients.Add(requesterUserId);
        }

        if (recipients.Count == 0)
        {
            return;
        }

        await hub.Clients.Users(recipients).SendAsync(BeaconHubEventNames.ApprovalUpdated, payload, cancellationToken);
    }
}
