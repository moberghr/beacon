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
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var recipient = new Recipient
        {
            Name = recipientData.Name,
            Description = recipientData.Description,
            Destination = recipientData.Destination,
            NotificationType = recipientData.NotificationType,
            HeadersJson = recipientData.HeadersJson
        };

        context.Recipients.Add(recipient);
        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Success = true
        };
    }

    public async Task DeleteRecipient(int recipientId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var recipient = await context.Recipients
            .Where(x => x.Id == recipientId)
            .SingleAsync(cancellationToken);

        if (recipient.Subscriptions.Count > 0)
        {
            throw new SemanticoException($"Unable to remove recipient due to existing subscriptions");
        }

        recipient.Archive();
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
                HeadersJson = x.HeadersJson
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<BaseResponse> UpdateRecipient(RecipientData recipientData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var recipient = await context.Recipients
            .Where(x => x.Id == recipientData.RecipientId)
            .SingleAsync(cancellationToken);

        recipient.Name = recipientData.Name;
        recipient.NotificationType = recipientData.NotificationType;
        recipient.Destination = recipientData.Destination;
        recipient.Description = recipientData.Description;
        recipient.HeadersJson = recipientData.HeadersJson;

        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Success = true
        };
    }
}