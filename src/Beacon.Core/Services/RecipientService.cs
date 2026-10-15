using Microsoft.EntityFrameworkCore;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Notifications;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Recipients;

namespace Beacon.Core.Services;

public interface IRecipientService
{
    Task<BaseResponse> CreateRecipient(RecipientData recipientData, CancellationToken cancellationToken);

    Task<BaseResponse> UpdateRecipient(RecipientData recipientData, CancellationToken cancellationToken);

    Task DeleteRecipient(int recipientId, CancellationToken cancellationToken);

    Task<List<RecipientData>> GetRecipients(int? recipientId, string? searchQuery, CancellationToken cancellationToken);
}

/// <summary>
/// Host-facing recipient service. Like the REST handlers it checks the destination policy, stores secrets encrypted
/// and returns them masked (<see cref="RecipientSecrets"/>); a destination, header or mask rule it breaks comes back as
/// an unsuccessful <see cref="BaseResponse"/> with the reason, like an invalid body template. Authorization is the
/// calling host's responsibility. While the change approval workflow covers recipients, a recipient created or whose
/// delivery changes receives nothing until a second person approves it, as through the REST handlers
/// (<see cref="RecipientChangeApproval"/>): the request's caller is the requester, a caller Beacon cannot name is refused
/// (<see cref="BeaconForbiddenException"/>), and a change while someone else's waits comes back unsuccessful.
/// </summary>
internal class RecipientService(
    IDbContextFactory<BeaconContext> contextFactory,
    RecipientSecretEditor secretEditor,
    RecipientChangeApproval changeApproval,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider) : IRecipientService
{
    public async Task<BaseResponse> CreateRecipient(RecipientData recipientData, CancellationToken cancellationToken)
    {
        var templateValidation = ValidateBodyTemplate(recipientData.BodyTemplate);
        if (templateValidation != null && !templateValidation.Success)
            return templateValidation;

        string destination;
        string? headersJson;
        try
        {
            (destination, headersJson) = secretEditor.Prepare(recipientData.NotificationType, recipientData.Destination, recipientData.HeadersJson, stored: null);
        }
        catch (InvalidOperationException ex)
        {
            return new BaseResponse { Success = false, Message = ex.Message };
        }

        var requester = await changeApproval.RequesterAsync(null, cancellationToken);
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Recipient NewRecipient() => new()
        {
            Name = recipientData.Name,
            Description = recipientData.Description,
            Destination = destination,
            NotificationType = recipientData.NotificationType,
            HeadersJson = headersJson,
            BodyTemplate = recipientData.BodyTemplate,
            ApprovedDestinationHash = RecipientChangeApproval.ApprovedHashOfNew(requester),
            CreatedByUserId = actor.UserId
        };

        if (requester == null)
        {
            var recipient = NewRecipient();
            context.Recipients.Add(recipient);
            await securityAudit.SaveWithEventsAsync(
                context,
                () => [RecipientAuditEntries.Created(recipient.Id, recipient.NotificationType, null)],
                cancellationToken);
        }
        else
        {
            // The recipient's save gives it the id its request names; both commit together.
            var strategy = context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                var recipient = NewRecipient();
                context.Recipients.Add(recipient);
                await context.SaveChangesAsync(cancellationToken);

                var submission = await changeApproval.SubmitNewAsync(context, requester, recipient, cancellationToken);
                await securityAudit.SaveWithEventsAsync(
                    context,
                    () => RecipientAuditEntries.With(RecipientAuditEntries.Created(recipient.Id, recipient.NotificationType, submission), submission),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });
        }

        // A recipient submitted for approval says so: it receives nothing until approved.
        return new BaseResponse
        {
            Success = true,
            Message = requester == null
                ? templateValidation?.Message
                : string.Join(" ", new[] { templateValidation?.Message, Handlers.Recipients.CreateRecipientResult.SubmittedForApprovalMessage }.Where(x => !string.IsNullOrEmpty(x)))
        };
    }

    public async Task DeleteRecipient(int recipientId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var recipient = await context.Recipients
            .Where(x => x.Id == recipientId)
            .Select(r => new
            {
                Entity = r,
                HasSubscriptions = r.Subscriptions.Any(),
                HasDataContracts = r.DataContracts.Any()
            })
            .SingleAsync(cancellationToken);

        if (recipient.HasSubscriptions)
        {
            throw new BeaconException($"Unable to remove recipient due to existing subscriptions");
        }

        if (recipient.HasDataContracts)
        {
            throw new BeaconException($"Unable to remove recipient due to existing data contracts");
        }

        recipient.Entity.Archive();
        recipient.Entity.RecordChangeBy(await actorAccessor.GetCurrentAsync(cancellationToken), timeProvider);
        await securityAudit.StageAsync(context, RecipientAuditEntries.Deleted(recipientId), cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<RecipientData>> GetRecipients(int? recipientId, string? searchQuery, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Destinations are encrypted at rest, so search covers the name, description and type only.
        var recipients = await context.Recipients
            .WhereIf(recipientId.HasValue, x => x.Id == recipientId)
            .WhereIf(!string.IsNullOrWhiteSpace(searchQuery),
                x => (x.Name + x.Description + x.NotificationType)
                .Contains(searchQuery, StringComparison.CurrentCultureIgnoreCase))
            .Select(x => new RecipientData
            {
                RecipientId = x.Id,
                Name = x.Name,
                Description = x.Description,
                Destination = x.Destination,
                NotificationType = x.NotificationType,
                HeadersJson = x.HeadersJson,
                BodyTemplate = x.BodyTemplate
            })
            .ToListAsync(cancellationToken);

        foreach (var recipient in recipients)
        {
            var masked = secretEditor.ReadMasked(recipient.NotificationType, recipient.Destination, recipient.HeadersJson);
            recipient.Destination = masked.Destination;
            recipient.HeadersJson = masked.HeadersJson;
        }

        return recipients;
    }

    public async Task<BaseResponse> UpdateRecipient(RecipientData recipientData, CancellationToken cancellationToken)
    {
        var templateValidation = ValidateBodyTemplate(recipientData.BodyTemplate);
        if (templateValidation != null && !templateValidation.Success)
            return templateValidation;

        var requester = await changeApproval.RequesterAsync(recipientData.RecipientId, cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        BaseResponse? refusal;
        if (requester == null)
        {
            // Without the workflow nothing is submitted; a pending request the change made moot was superseded, and is told.
            ChangeSubmission? superseded;
            (refusal, superseded) = await UpdateAsync(context, recipientData, null, cancellationToken);
            await changeApproval.NotifySupersededAsync(superseded, cancellationToken);
        }
        else
        {
            // Under the workflow: the recipient's row first (as a decision takes it), then the change and its request,
            // committed together; a refused change commits nothing.
            ChangeSubmission? submission = null;
            refusal = null;
            var strategy = context.Database.CreateExecutionStrategy();
            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                    await RecipientChangeApproval.LockAsync(context, recipientData.RecipientId ?? 0, cancellationToken);
                    (refusal, submission) = await UpdateAsync(context, recipientData, requester, cancellationToken);
                    if (refusal == null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }
                });
            }
            catch (BeaconConflictException ex)
            {
                return new BaseResponse { Success = false, Message = ex.Message };
            }

            await changeApproval.NotifySupersededAsync(submission, cancellationToken);
        }

        return refusal ?? new BaseResponse
        {
            Success = true,
            Message = templateValidation?.Message
        };
    }

    // Stores the change (under the workflow, also submitted for approval); an unsuccessful response, and nothing stored,
    // when the submission breaks a destination, header or mask rule.
    private async Task<(BaseResponse? Refusal, ChangeSubmission? Submission)> UpdateAsync(
        BeaconContext context,
        RecipientData recipientData,
        ChangeRequester? requester,
        CancellationToken cancellationToken)
    {
        var recipient = await context.Recipients
            .Where(x => x.Id == recipientData.RecipientId)
            .SingleAsync(cancellationToken);

        var stored = new StoredRecipientSecrets(recipient.NotificationType, recipient.Destination, recipient.HeadersJson);
        string destination;
        string? headersJson;
        try
        {
            (destination, headersJson) = secretEditor.Prepare(
                recipientData.NotificationType,
                recipientData.Destination,
                recipientData.HeadersJson,
                stored);
        }
        catch (InvalidOperationException ex)
        {
            return (new BaseResponse { Success = false, Message = ex.Message }, null);
        }

        var delivery = secretEditor.DeliveryChangeOf(recipientData.NotificationType, recipientData.Destination, recipientData.HeadersJson, stored);
        var fields = new RecipientFieldChanges(
            !string.Equals(recipient.Name, recipientData.Name, StringComparison.Ordinal),
            !string.Equals(recipient.Description, recipientData.Description, StringComparison.Ordinal),
            !string.Equals(recipient.BodyTemplate, recipientData.BodyTemplate, StringComparison.Ordinal));
        recipient.Name = recipientData.Name;
        recipient.NotificationType = recipientData.NotificationType;
        recipient.Destination = destination;
        recipient.Description = recipientData.Description;
        recipient.HeadersJson = headersJson;
        recipient.BodyTemplate = recipientData.BodyTemplate;

        var submission = await changeApproval.SubmitUpdateAsync(context, requester, recipient, stored, cancellationToken);
        if (!delivery.Changed && !fields.Any)
        {
            await context.SaveChangesAsync(cancellationToken);

            return (null, submission);
        }

        recipient.RecordChangeBy(await actorAccessor.GetCurrentAsync(cancellationToken), timeProvider);
        await securityAudit.SaveWithEventsAsync(
            context,
            () => RecipientAuditEntries.With(
                RecipientAuditEntries.Updated(recipient.Id, recipient.NotificationType, delivery, fields, submission),
                submission),
            cancellationToken);

        return (null, submission);
    }

    private static BaseResponse? ValidateBodyTemplate(string? bodyTemplate)
    {
        if (string.IsNullOrWhiteSpace(bodyTemplate))
            return null;

        var validationResult = Adapters.Shared.TemplateValidator.ValidateWithPlaceholderCheck(bodyTemplate);

        if (!validationResult.IsValid)
        {
            return new BaseResponse
            {
                Success = false,
                Message = $"Invalid body template: {validationResult.ErrorMessage}"
            };
        }

        if (!string.IsNullOrWhiteSpace(validationResult.WarningMessage))
        {
            return new BaseResponse
            {
                Success = true,
                Message = validationResult.WarningMessage
            };
        }

        return null;
    }
}