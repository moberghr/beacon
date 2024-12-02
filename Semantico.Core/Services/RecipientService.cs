using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.Recipients;

namespace Semantico.Core.Services
{
    public interface IRecipientService
    {
        Task<BaseResponse> CreateRecipient(RecipientData recipientData, CancellationToken cancellationToken);

        Task<BaseResponse> UpdateRecipient(RecipientData recipientData, CancellationToken cancellationToken);

        Task DeleteRecipient(int recipientId, CancellationToken cancellationToken);

        Task<List<RecipientData>> GetRecipients(int? recipientId, CancellationToken cancellationToken);
    }

    internal class RecipientService : IRecipientService
    {
        private readonly SemanticoContext _context;

        public RecipientService(SemanticoContext context)
        {
            _context = context;
        }

        public async Task<BaseResponse> CreateRecipient(RecipientData recipientData, CancellationToken cancellationToken)
        {
            var recipient = new Recipient
            {
                Name = recipientData.Name,
                Description = recipientData.Description,
                Destination = recipientData.Destination,
                NotificationType = recipientData.NotificationType,
            };

            _context.Recipients.Add(recipient);
            await _context.SaveChangesAsync(cancellationToken);

            return new BaseResponse
            {
                Success = true
            };
        }

        public async Task DeleteRecipient(int recipientId, CancellationToken cancellationToken)
        {
            var recipient = await _context.Recipients
                .Where(x => x.Id == recipientId)
                .SingleAsync(cancellationToken);

            if (recipient.Subscriptions.Count > 0)
            {
                throw new SemanticoException($"Unable to remove recipient due to existing subscriptions");
            }

            recipient.Archive();
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task<List<RecipientData>> GetRecipients(int? recipientId, CancellationToken cancellationToken)
        {
            return await _context.Recipients
                .WhereIf(recipientId.HasValue, x => x.Id == recipientId)
                .Select(x => new RecipientData
                {
                    RecipientId = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    Destination = x.Destination,
                    NotificationType= x.NotificationType,
                })
                .ToListAsync(cancellationToken);
        }

        public async Task<BaseResponse> UpdateRecipient(RecipientData recipientData, CancellationToken cancellationToken)
        {
            var project = await _context.Recipients
                .Where(x => x.Id == recipientData.RecipientId)
                .SingleAsync(cancellationToken);

            project.Name = recipientData.Name;
            project.NotificationType = recipientData.NotificationType;
            project.Destination = recipientData.Destination;
            project.Description = recipientData.Description;

            await _context.SaveChangesAsync(cancellationToken);

            return new BaseResponse
            {
                Success = true
            };
        }
    }
}
