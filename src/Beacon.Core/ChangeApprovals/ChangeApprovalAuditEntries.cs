using Beacon.Core.Data.Enums;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// The security audit events of the change approval workflow (migration jobs, Custom SQL rules, recipient destinations):
/// a submission, by its requester (the caller), and a decision, by its reviewer (the caller), naming the request, its
/// kind and the resource it changes, and the requester by <c>Users.Id</c> — never the proposed definition or a comment.
/// </summary>
internal static class ChangeApprovalAuditEntries
{
    /// <summary>
    /// The request <paramref name="submission"/> added, after the save that gave it its id; nothing when it added none (the
    /// change needs no approval, or a pending request already proposes it).
    /// </summary>
    public static IEnumerable<SecurityAuditEntry> Submitted(ChangeSubmission? submission)
    {
        if (submission?.Request is not { } request)
        {
            return [];
        }

        return
        [
            new SecurityAuditEntry(SecurityAuditActions.ChangeSubmitted)
                .On(SecurityAuditResources.ChangeApprovalRequest, request.Id)
                .With(new SecurityAuditDetails()
                    .Add("kind", request.Kind)
                    .Add("resourceId", request.ResourceId)
                    .Add("supersededRequestIds", submission.Superseded.Select(x => x.Id)))
        ];
    }

    /// <summary>A decision on request <paramref name="requestId"/>: approved or rejected (a requester's withdrawal included).</summary>
    public static SecurityAuditEntry Decided(
        int requestId,
        ChangeApprovalKind kind,
        int resourceId,
        ApprovalStatus outcome,
        string? requesterExternalId)
    {
        var action = outcome == ApprovalStatus.Approved ? SecurityAuditActions.ChangeApproved : SecurityAuditActions.ChangeRejected;

        return new SecurityAuditEntry(action)
            .On(SecurityAuditResources.ChangeApprovalRequest, requestId)
            .With(new SecurityAuditDetails()
                .Add("kind", kind)
                .Add("resourceId", resourceId)
                .Add("outcome", outcome))
            .Naming("requesterUserId", requesterExternalId);
    }
}
