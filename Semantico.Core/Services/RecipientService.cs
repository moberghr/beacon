using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.Recipients;

namespace Semantico.Core.Services;

public interface IRecipientService
{
    Task<BaseResponse> CreateRecipient(RecipientData recipientData, CancellationToken cancellationToken);

    Task<BaseResponse> UpdateRecipient(RecipientData recipientData, CancellationToken cancellationToken);

    Task DeleteRecipient(int recipientId, CancellationToken cancellationToken);
    
    Task<List<RecipientData>> GetRecipients(int? recipientId, string? searchQuery, CancellationToken cancellationToken);
}

internal class RecipientService(IDbContextFactory<SemanticoContext> contextFactory) : IRecipientService
{
    public async Task<BaseResponse> CreateRecipient(RecipientData recipientData, CancellationToken cancellationToken)
    {
        var templateValidation = ValidateBodyTemplate(recipientData.BodyTemplate);
        if (templateValidation != null && !templateValidation.Success)
            return templateValidation;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var recipient = new Recipient
        {
            Name = recipientData.Name,
            Description = recipientData.Description,
            Destination = recipientData.Destination,
            NotificationType = recipientData.NotificationType,
            HeadersJson = recipientData.HeadersJson,
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
            throw new SemanticoException($"Unable to remove recipient due to existing subscriptions");
        }

        if (recipient.HasDataContracts)
        {
            throw new SemanticoException($"Unable to remove recipient due to existing data contracts");
        }

        recipient.Entity.Archive();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<RecipientData>> GetRecipients(int? recipientId, string? searchQuery, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        return await context.Recipients
            .WhereIf(recipientId.HasValue, x => x.Id == recipientId)
            .WhereIf(!string.IsNullOrWhiteSpace(searchQuery),
                x => (x.Name + x.Description + x.NotificationType + x.Destination)
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

        recipient.Name = recipientData.Name;
        recipient.NotificationType = recipientData.NotificationType;
        recipient.Destination = recipientData.Destination;
        recipient.Description = recipientData.Description;
        recipient.HeadersJson = recipientData.HeadersJson;
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