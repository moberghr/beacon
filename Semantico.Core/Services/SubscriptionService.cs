using Microsoft.EntityFrameworkCore;
using Cronos;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Recipients;
using Semantico.Core.Models.Subscriptions;
using Semantico.Core.Validators;
using Semantico.Core.Worker;

namespace Semantico.Core.Services;

public interface ISubscriptionService
{
    Task<BaseResponse> CreateSubscription(SubscriptionData subscriptionData, CancellationToken cancellationToken);

    Task UpdateSubscription(SubscriptionData subscriptionData, CancellationToken cancellationToken);

    Task DeleteSubscription(int subscriptionId, CancellationToken cancellationToken);

    Task AddRecipients(int subscriptionId, List<int> recipientIds, CancellationToken cancellationToken);

    Task RemoveRecipient(int subscriptionId, int recipientId, CancellationToken cancellationToken);

    Task<List<SubscriptionData>> GetSubscriptions(int? subscriptionId, int? queryId, NotificationType? notificationType, string keyword, CancellationToken cancellationToken);

    Task<SubscriptionDetailsData> GetSubscriptionDetails(int subscriptionId, CancellationToken cancellationToken);
}

internal class SubscriptionService : ISubscriptionService
{
    private readonly SemanticoContext _context;
    private readonly ISemanticoScheduler _semanticoScheduler;

    public SubscriptionService(SemanticoContext context, ISemanticoScheduler semanticoScheduler)
    {
        _context = context;
        _semanticoScheduler = semanticoScheduler;
    }

    public async Task<BaseResponse> CreateSubscription(SubscriptionData subscriptionData, CancellationToken cancellationToken)
    {
        CronExpression.Parse(subscriptionData.CronExpression);

        var queryParams = await _context.QueryParameters
            .Where(x => x.QueryId == subscriptionData.QueryId)
            .Select(x =>
                new QueryParameterData
                {
                    Description = x.Description,
                    Placeholder = x.Placeholder,
                    Name = x.Name,
                    Type = x.Type
                })
            .ToListAsync(cancellationToken);

        SubscriptionValidator.ValidateParameters(subscriptionData.Parameters, queryParams);

        var recipients = await _context.Recipients
            .Where(x => subscriptionData.Recipients.Select(y => y.RecipientId).Contains(x.Id))
            .ToListAsync(cancellationToken);

        var subscription = new Subscription
        {
            CronExpression = subscriptionData.CronExpression,
            QueryId = subscriptionData.QueryId,
            MaxRows = subscriptionData.MaxRows,
            IncludeAttachment = subscriptionData.IncludeAttachment,
            ShowQuery = subscriptionData.ShowQuery,
            Recipients = recipients,
            Parameters = subscriptionData.Parameters.Select(x =>
                new SubscriptionParameter
                {
                    QueryPlaceholder = x.QueryPlaceholder,
                    Value = x.Value,
                }).ToList()
        };

        _context.Subscriptions.Add(subscription);

        await _context.SaveChangesAsync(cancellationToken);

        var query = _context.Queries.Single(x => x.Id == subscriptionData.QueryId);

        _semanticoScheduler.AddOrUpdate(subscription.Id, $"{query.Name}: {subscription.Id}", subscription.CronExpression);

        return new BaseResponse { Success = true, Message = "Subscription created successfully" };
    }

    public async Task DeleteSubscription(int subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await _context.Subscriptions
            .Include(x => x.Parameters)
            .Include(x => x.Query)
            .Where(x => x.Id == subscriptionId)
            .SingleAsync(cancellationToken);

        subscription.Archive();

        _semanticoScheduler.Remove(subscription.Id, $"{subscription.Query.Name}: {subscription.Id}");

        foreach (var param in subscription.Parameters)
        {
            param.Archive();
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<SubscriptionData>> GetSubscriptions(int? subscriptionId, int? queryId, NotificationType? notificationType, string? keyword, CancellationToken cancellationToken)
    {
        return await _context.Subscriptions
            .WhereIf(subscriptionId.HasValue, x => x.Id == subscriptionId)
            .WhereIf(queryId.HasValue, x => x.QueryId == queryId)
            .WhereIf(!string.IsNullOrWhiteSpace(keyword), x => x.Recipients.Select(y => y.Name).Contains(keyword!))
            .Select(x =>
                new SubscriptionData
                {
                    SubscriptionId = x.Id,
                    QueryId = x.QueryId,
                    QueryName = x.Query.Name,
                    Recipients = x.Recipients.Select(y => new RecipientData
                    {
                        RecipientId = y.Id,
                        Name = y.Name,
                        Description = y.Description,
                        Destination = y.Destination,
                        NotificationType = y.NotificationType,
                        ResultAttachmentType = y.ResultAttachmentType
                    }).ToList(),
                    CronExpression = x.CronExpression,
                    MaxRows = x.MaxRows,
                    IncludeAttachment = x.IncludeAttachment,
                    ShowQuery = x.ShowQuery,
                    Parameters = x.Parameters.Select(y => new SubscriptionParamaterData
                    {
                        QueryPlaceholder = y.QueryPlaceholder,
                        Value = y.Value
                    }).ToList()
                })
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateSubscription(SubscriptionData subscriptionData, CancellationToken cancellationToken)
    {
        CronExpression.Parse(subscriptionData.CronExpression);

        var subscription = await _context.Subscriptions
            .Include(subscription => subscription.Parameters)
            .Where(x => x.Id == subscriptionData.SubscriptionId)
            .SingleAsync(cancellationToken);

        var queryParams = await _context.QueryParameters
            .Where(x => x.QueryId == subscription.QueryId)
            .Select(x =>
                new QueryParameterData
                {
                    Type = x.Type,
                    Name = x.Name,
                    Description = x.Description,
                    Placeholder = x.Placeholder,
                })
            .ToListAsync(cancellationToken);

        var recipients = await _context.Recipients
            .Where(x => subscriptionData.Recipients.Select(y => y.RecipientId).Contains(x.Id))
            .ToListAsync(cancellationToken);

        SubscriptionValidator.ValidateParameters(subscriptionData.Parameters, queryParams);

        var shouldUpdateHangfire = subscription.CronExpression != subscriptionData.CronExpression;

        subscription.CronExpression = subscriptionData.CronExpression;
        subscription.MaxRows = subscriptionData.MaxRows;
        subscription.IncludeAttachment = subscriptionData.IncludeAttachment;
        subscription.ShowQuery = subscriptionData.ShowQuery;
        subscription.Recipients = recipients;

        foreach (var subscriptionParameter in subscription.Parameters)
        {
            subscriptionParameter.Archive();
        }

        foreach (var subscriptionParameter in subscriptionData.Parameters)
        {
            var subscriptionParam = new SubscriptionParameter
            {
                SubscriptionId = subscription.Id,
                QueryPlaceholder = subscriptionParameter.QueryPlaceholder,
                Value = subscriptionParameter.Value
            };

            _context.SubscriptionParameters.Add(subscriptionParam);
        }

        await _context.SaveChangesAsync(cancellationToken);

        var query = _context.Queries.Single(x => x.Id == subscription.QueryId);

        if (shouldUpdateHangfire)
        {
            _semanticoScheduler.AddOrUpdate(subscription.Id, $"{query.Name}: {subscription.Id}", subscription.CronExpression);
        }
    }

    public async Task<SubscriptionDetailsData> GetSubscriptionDetails(int subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await _context.Subscriptions
            .IgnoreQueryFilters()
            .Where(x => x.Id == subscriptionId)
            .Select(x => new SubscriptionDetailsData
            {
                SubscriptionId = x.Id,
                QueryId = x.QueryId,
                Recipients = x.Recipients.Select(y => new RecipientData
                {
                    RecipientId = y.Id,
                    Name = y.Name,
                    Description = y.Description,
                    Destination = y.Destination,
                    NotificationType = y.NotificationType,
                    ResultAttachmentType = y.ResultAttachmentType
                }).ToList(),
                QueryName = x.Query.Name,
                CronExpression = x.CronExpression,
                MaxRows = x.MaxRows,
                IncludeAttachment = x.IncludeAttachment,
                ShowQuery = x.ShowQuery,
                Status = x.ArchivedTime.HasValue ? "Archived" : "Active",
                Parameters = x.Parameters.Select(y => new SubscriptionParamaterData()
                {
                    QueryPlaceholder = y.QueryPlaceholder,
                    Value = y.Value
                }).ToList(),
            })
            .SingleAsync(cancellationToken);

        return subscription;
    }

    public async Task RemoveRecipient(int subscriptionId, int recipientId, CancellationToken cancellationToken)
    {
        var subscription = await _context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .Include(x => x.Recipients)
            .SingleAsync(cancellationToken);

        subscription.Recipients = subscription.Recipients.Where(x => x.Id != recipientId).ToList();

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRecipients(int subscriptionId, List<int> recipientIds, CancellationToken cancellationToken)
    {
        var subscription = await _context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .Include(x => x.Recipients)
            .SingleAsync(cancellationToken);

        recipientIds = recipientIds.Except(subscription.Recipients.Select(x => x.Id)).ToList();

        var recipients = await _context.Recipients
            .Where(x => recipientIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        subscription.Recipients.AddRange(recipients);

        await _context.SaveChangesAsync(cancellationToken);
    }
}