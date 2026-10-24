using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Recipients;

/// <summary>
/// For an Admin or a SecurityAdmin (the endpoint carries the SecurityAdmin policy). The destination and headers must pass
/// the destination policy and are stored encrypted. While the change approval workflow covers recipients, the recipient
/// receives nothing until a second person approves where it delivers (<see cref="RecipientChangeApproval"/>): the caller
/// is the request's requester.
/// </summary>
internal sealed class CreateRecipientHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    RecipientSecretEditor secretEditor,
    RecipientChangeApproval changeApproval,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit)
    : IRequestHandler<CreateRecipientCommand, CreateRecipientResult>
{
    public async Task<CreateRecipientResult> Handle(CreateRecipientCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Recipient name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Destination))
        {
            throw new InvalidOperationException("Recipient destination is required.");
        }

        if (!Enum.IsDefined(typeof(NotificationType), request.NotificationType))
        {
            throw new InvalidOperationException("Unknown notification type.");
        }

        var notificationType = (NotificationType)request.NotificationType;
        var (destination, headersJson) = secretEditor.Prepare(notificationType, request.Destination, request.HeadersJson, stored: null);
        var requester = await changeApproval.RequesterAsync(null, cancellationToken);
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var nameTaken = await context.Recipients
            .Where(x => x.Name == request.Name)
            .AnyAsync(cancellationToken);

        if (nameTaken)
        {
            throw new InvalidOperationException($"A recipient named '{request.Name}' already exists.");
        }

        Recipient NewRecipient() => new()
        {
            Name = request.Name,
            Description = request.Description,
            Destination = destination,
            NotificationType = notificationType,
            HeadersJson = headersJson,
            BodyTemplate = request.BodyTemplate,
            ApprovedDestinationHash = RecipientChangeApproval.ApprovedHashOfNew(requester),
            CreatedByUserId = actor.UserId
        };

        if (requester == null)
        {
            var entity = NewRecipient();
            context.Recipients.Add(entity);
            await securityAudit.SaveWithEventsAsync(
                context,
                () => [RecipientAuditEntries.Created(entity.Id, notificationType, null)],
                cancellationToken);

            return new CreateRecipientResult(entity.Id);
        }

        return new CreateRecipientResult(await CreateForApprovalAsync(context, requester, NewRecipient, cancellationToken))
        {
            AwaitingApproval = true,
            Message = CreateRecipientResult.SubmittedForApprovalMessage
        };
    }

    // The recipient and its request in one unit of work: the recipient's save gives it the id its request names, and both
    // commit together. Through the execution strategy, so a host that enables retry-on-failure gets a retried unit; a
    // retry starts from a clean tracker and a new recipient.
    private async Task<int> CreateForApprovalAsync(
        BeaconContext context,
        ChangeRequester requester,
        Func<Recipient> newRecipient,
        CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var entity = newRecipient();
            context.Recipients.Add(entity);
            await context.SaveChangesAsync(cancellationToken);

            var submission = await changeApproval.SubmitNewAsync(context, requester, entity, cancellationToken);
            await securityAudit.SaveWithEventsAsync(
                context,
                () => RecipientAuditEntries.With(RecipientAuditEntries.Created(entity.Id, entity.NotificationType, submission), submission),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return entity.Id;
        });
    }
}

[ProjectScopeExempt("recipients are project-less; changing them is gated by the SecurityAdmin permission", Policy = BeaconPermissions.SecurityAdminPolicy)]
public record CreateRecipientCommand(
    string Name,
    string? Description,
    string Destination,
    int NotificationType,
    string? HeadersJson,
    string? BodyTemplate) : IRequest<CreateRecipientResult>
{
    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Description = {Description}, Destination = {RedactedPrint.Of(Destination)}, NotificationType = {NotificationType}, HeadersJson = {RedactedPrint.Of(HeadersJson)}, BodyTemplate = {BodyTemplate}");

        return true;
    }
}

public record CreateRecipientResult(int Id)
{
    /// <summary>What a create says when the recipient was submitted for approval.</summary>
    public const string SubmittedForApprovalMessage =
        "Submitted for approval: the recipient receives no deliveries until a second person approves where it delivers.";

    /// <summary>The recipient was submitted for approval and receives no deliveries until approved.</summary>
    public bool AwaitingApproval { get; init; }

    /// <summary>What happens next, when the recipient waits for approval; null otherwise.</summary>
    public string? Message { get; init; }
}
