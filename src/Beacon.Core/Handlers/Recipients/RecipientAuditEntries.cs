using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Recipients;

/// <summary>
/// The security audit events of a recipient's lifecycle, shared by the REST handlers and the host-facing
/// <c>RecipientService</c>: its id, its type, whether where it delivers or a secret changed, and the change request a
/// destination waits on — never its name, destination, headers or body template.
/// </summary>
internal static class RecipientAuditEntries
{
    public static SecurityAuditEntry Created(int recipientId, NotificationType type, ChangeSubmission? submission)
    {
        return new SecurityAuditEntry(SecurityAuditActions.RecipientCreated)
            .On(SecurityAuditResources.Recipient, recipientId)
            .With(new SecurityAuditDetails()
                .Add("notificationType", type)
                .Add("changeApprovalId", submission?.Request?.Id));
    }

    public static SecurityAuditEntry Updated(
        int recipientId,
        NotificationType type,
        RecipientDeliveryChange delivery,
        RecipientFieldChanges fields,
        ChangeSubmission? submission)
    {
        return new SecurityAuditEntry(SecurityAuditActions.RecipientUpdated)
            .On(SecurityAuditResources.Recipient, recipientId)
            .With(new SecurityAuditDetails()
                .Add("notificationType", type)
                .Add("destinationChanged", delivery.DestinationChanged)
                .Add("secretChanged", delivery.SecretChanged)
                .Add("nameChanged", fields.Name)
                .Add("descriptionChanged", fields.Description)
                .Add("bodyTemplateChanged", fields.BodyTemplate)
                .Add("changeApprovalId", submission?.Request?.Id));
    }

    public static SecurityAuditEntry Deleted(int recipientId)
    {
        return new SecurityAuditEntry(SecurityAuditActions.RecipientDeleted).On(SecurityAuditResources.Recipient, recipientId);
    }

    /// <summary>The submission's events: the recipient's, then the change request it submitted, when it submitted one.</summary>
    public static IEnumerable<SecurityAuditEntry> With(SecurityAuditEntry recipientEvent, ChangeSubmission? submission)
    {
        return new[] { recipientEvent }.Concat(ChangeApprovalAuditEntries.Submitted(submission));
    }
}

/// <summary>Which of a recipient's own fields a save changes (not where it delivers).</summary>
internal sealed record RecipientFieldChanges(bool Name, bool Description, bool BodyTemplate)
{
    public bool Any => Name || Description || BodyTemplate;
}
