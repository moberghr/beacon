namespace Beacon.Core.Services;

/// <summary>
/// Publishes real-time approval status changes to the affected users. The concrete
/// implementation lives in the host/API layer (SignalR), so <c>Beacon.Core</c> does not
/// depend on the transport. Handlers call this after an approve/reject decision commits.
/// </summary>
public interface IApprovalNotifier
{
    /// <summary>
    /// Notifies the reviewer and (when different) the original requester that the approval
    /// request changed status. Implementations must no-op when both ids are null/empty.
    /// </summary>
    Task ApprovalUpdatedAsync(
        int approvalId,
        string status,
        string? reviewerUserId,
        string? requesterUserId,
        CancellationToken cancellationToken = default);
}
