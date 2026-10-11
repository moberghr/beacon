using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Recipients;

/// <summary>
/// For an Admin or a SecurityAdmin (the endpoint carries the SecurityAdmin policy). Reads return masked secrets, so a
/// destination or header value echoed back masked (or left empty) keeps the stored secret while the destination stays
/// the same (<see cref="RecipientSecrets"/>); whatever is stored must pass the destination policy and is stored
/// encrypted. Anyone but an Admin changes where a recipient delivers (its type, destination or headers) only when it can
/// read every subscription and data contract the recipient is attached to (403 otherwise): a destination decides where
/// those send their results.
/// </summary>
internal sealed class UpdateRecipientHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    RecipientSecretEditor secretEditor,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
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

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

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

        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        if (!permissions.IsAdmin && secretEditor.ChangesDelivery(notificationType, request.Destination, request.HeadersJson, stored))
        {
            await DemandReadsEveryAttachedAsync(context, entity.Id, cancellationToken);
        }

        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.Destination = destination;
        entity.NotificationType = notificationType;
        entity.HeadersJson = headersJson;
        entity.BodyTemplate = request.BodyTemplate;

        await context.SaveChangesAsync(cancellationToken);
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
    string? BodyTemplate) : IRequest;
