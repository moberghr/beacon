using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Microsoft.Extensions.Logging;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Recipients;

/// <summary>
/// For an Admin or a SecurityAdmin (the endpoint carries the SecurityAdmin policy). Reads return masked secrets, so a
/// destination or header value echoed back masked (or left empty) keeps the stored secret while the destination stays
/// the same (<see cref="RecipientSecrets"/>); whatever is stored must pass the destination policy and is stored
/// encrypted. Anyone but an Admin changes where a recipient delivers (its type, destination or headers) only when it can
/// read every subscription and data contract the recipient is attached to (403 otherwise): a destination decides where
/// those send their results. While the change approval workflow covers recipients, a change of where it delivers is
/// submitted for approval in the unit of work that stores it, the recipient's row taken first, and the recipient receives
/// nothing until it is approved (<see cref="RecipientChangeApproval"/>).
/// </summary>
internal sealed class UpdateRecipientHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    RecipientSecretEditor secretEditor,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    RecipientChangeApproval changeApproval,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider,
    ILogger<UpdateRecipientHandler> logger)
    : IRequestHandler<UpdateRecipientCommand>
{
    public async Task Handle(UpdateRecipientCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Recipient name is required.");
        }

        if (!Enum.IsDefined(typeof(NotificationType), request.NotificationType))
        {
            throw new InvalidOperationException("Unknown notification type.");
        }

        var requester = await changeApproval.RequesterAsync(request.Id, cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Without the workflow nothing is submitted; a pending request the change made moot was superseded, and is told.
        if (requester == null)
        {
            await changeApproval.NotifySupersededAsync(await UpdateAsync(context, request, null, cancellationToken), cancellationToken);
            return;
        }

        // Under the workflow: the recipient's row first (as a decision takes it), then the change and its request,
        // committed together. Through the execution strategy, so a host that enables retry-on-failure gets a retried unit;
        // a retry starts from a clean tracker.
        ChangeSubmission? submission = null;
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            await RecipientChangeApproval.LockAsync(context, request.Id, cancellationToken);
            submission = await UpdateAsync(context, request, requester, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        await changeApproval.NotifySupersededAsync(submission, cancellationToken);
    }

    // Reads, checks and stores the change; under the workflow (a requester) it is also submitted for approval.
    private async Task<ChangeSubmission?> UpdateAsync(
        BeaconContext context,
        UpdateRecipientCommand request,
        ChangeRequester? requester,
        CancellationToken cancellationToken)
    {
        var entity = await context.Recipients
            .Where(x => x.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity == null)
        {
            throw new InvalidOperationException($"Recipient {request.Id} not found.");
        }

        var nameClash = await context.Recipients
            .Where(x => x.Id != request.Id)
            .Where(x => x.Name == request.Name)
            .AnyAsync(cancellationToken);

        if (nameClash)
        {
            throw new InvalidOperationException($"A recipient named '{request.Name}' already exists.");
        }

        var notificationType = (NotificationType)request.NotificationType;
        var stored = new StoredRecipientSecrets(entity.NotificationType, entity.Destination, entity.HeadersJson);
        var (destination, headersJson) = secretEditor.Prepare(notificationType, request.Destination, request.HeadersJson, stored);

        var delivery = secretEditor.DeliveryChangeOf(notificationType, request.Destination, request.HeadersJson, stored);
        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        if (!permissions.IsAdmin && delivery.Changed)
        {
            await DemandReadsEveryAttachedAsync(context, entity.Id, cancellationToken);
        }

        var fields = new RecipientFieldChanges(
            !string.Equals(entity.Name, request.Name, StringComparison.Ordinal),
            !string.Equals(entity.Description, request.Description, StringComparison.Ordinal),
            !string.Equals(entity.BodyTemplate, request.BodyTemplate, StringComparison.Ordinal));
        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.Destination = destination;
        entity.NotificationType = notificationType;
        entity.HeadersJson = headersJson;
        entity.BodyTemplate = request.BodyTemplate;

        var submission = await changeApproval.SubmitUpdateAsync(context, requester, entity, stored, cancellationToken);
        if (!delivery.Changed && !fields.Any)
        {
            // Saved as it was: nothing to attribute or record (a stored hash may still be rewritten under the current key).
            await context.SaveChangesAsync(cancellationToken);

            return submission;
        }

        entity.RecordChangeBy(await actorAccessor.GetCurrentAsync(cancellationToken), timeProvider);
        await securityAudit.SaveWithEventsAsync(
            context,
            () => RecipientAuditEntries.With(RecipientAuditEntries.Updated(entity.Id, notificationType, delivery, fields, submission), submission),
            cancellationToken);

        return submission;
    }

    // Every live subscription and data contract the recipient is attached to must be readable by the caller.
    private async Task DemandReadsEveryAttachedAsync(BeaconContext context, int recipientId, CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        var subscriptions = context.Subscriptions
            .Where(x => x.Recipients.Any(y => y.Id == recipientId));
        var contracts = context.DataContracts
            .Where(x => x.Recipients.Any(y => y.Id == recipientId));

        var attached = await subscriptions.CountAsync(cancellationToken) + await contracts.CountAsync(cancellationToken);
        var readableAttached = await subscriptions
                .WhereAllSourcesInOneProject(context, readable, x => x.Query.Steps.Select(y => y.DataSourceId), x => x.Query.OwnerUserId)
                .CountAsync(cancellationToken)
            + await contracts
                .WhereSourceAccessible(context, readable, x => x.DataSourceId)
                .CountAsync(cancellationToken);
        if (readableAttached < attached)
        {
            var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

            throw AccessRefusal.Of(
                logger,
                actor,
                "recipient destination",
                recipientId,
                "The recipient is attached to a subscription or data contract you cannot read; only an Admin can change where it delivers.");
        }
    }
}

/// <summary>An empty or masked <c>Destination</c>, or masked header values, keep the stored secrets.</summary>
[ProjectScopeExempt("recipients are project-less; changing them is gated by the SecurityAdmin permission, and a changed destination needs Read on everything the recipient is attached to (checked in the handler)", Policy = BeaconPermissions.SecurityAdminPolicy)]
public record UpdateRecipientCommand(
    int Id,
    string Name,
    string? Description,
    string? Destination,
    int NotificationType,
    string? HeadersJson,
    string? BodyTemplate) : IRequest
{
    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Id = {Id}, Name = {Name}, Description = {Description}, Destination = {RedactedPrint.Of(Destination)}, NotificationType = {NotificationType}, HeadersJson = {RedactedPrint.Of(HeadersJson)}, BodyTemplate = {BodyTemplate}");

        return true;
    }
}
