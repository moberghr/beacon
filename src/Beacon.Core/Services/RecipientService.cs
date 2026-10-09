using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Notifications;

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
/// calling host's responsibility.
/// </summary>
internal class RecipientService(
    IDbContextFactory<BeaconContext> contextFactory,
    RecipientSecretEditor secretEditor) : IRecipientService
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

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var recipient = new Recipient
        {
            Name = recipientData.Name,
            Description = recipientData.Description,
            Destination = destination,
            NotificationType = recipientData.NotificationType,
            HeadersJson = headersJson,
            BodyTemplate = recipientData.BodyTemplate
        };

        context.Recipients.Add(recipient);
        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Success = true,
            Message = templateValidation?.Message
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

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var recipient = await context.Recipients
            .Where(x => x.Id == recipientData.RecipientId)
            .SingleAsync(cancellationToken);

        string destination;
        string? headersJson;
        try
        {
            (destination, headersJson) = secretEditor.Prepare(
                recipientData.NotificationType,
                recipientData.Destination,
                recipientData.HeadersJson,
                new StoredRecipientSecrets(recipient.NotificationType, recipient.Destination, recipient.HeadersJson));
        }
        catch (InvalidOperationException ex)
        {
            return new BaseResponse { Success = false, Message = ex.Message };
        }

        recipient.Name = recipientData.Name;
        recipient.NotificationType = recipientData.NotificationType;
        recipient.Destination = destination;
        recipient.Description = recipientData.Description;
        recipient.HeadersJson = headersJson;
        recipient.BodyTemplate = recipientData.BodyTemplate;

        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Success = true,
            Message = templateValidation?.Message
        };
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