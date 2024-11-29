using Microsoft.EntityFrameworkCore;
using NCrontab.Advanced;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Subscriptions;
using Semantico.Core.Validators;
using Semantico.Core.Worker;

namespace Semantico.Core.Services;

public interface ISubscriptionService
{
    Task<BaseResponse> CreateSubscription(SubscriptionData subscriptionData, CancellationToken cancellationToken);

    Task UpdateSubscription(SubscriptionData subscriptionData, CancellationToken cancellationToken);

    Task DeleteSubscription(int subscriptionId, CancellationToken cancellationToken);

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
        CrontabSchedule.Parse(subscriptionData.CronExpression);

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

        var subscription = new Subscription
        {
            CronExpression = subscriptionData.CronExpression,
            QueryId = subscriptionData.QueryId,
            RecipientId = subscriptionData.RecipientId,
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
            .WhereIf(notificationType.HasValue, x => x.Recipient.NotificationType == notificationType)
            .WhereIf(!string.IsNullOrWhiteSpace(keyword), x => x.Recipient.Name.Contains(keyword!))
            .Select(x =>
                new SubscriptionData
                {
                    SubscriptionId = x.Id,
                    QueryId = x.QueryId,
                    RecipientId = x.RecipientId,
                    RecipientName = x.Recipient.Name,
                    CronExpression = x.CronExpression,
                    Parameters = x.Parameters.Select(y =>
                        new SubscriptionParamaterData
                        {
                            QueryPlaceholder = y.QueryPlaceholder,
                            Value = y.Value
                        }).ToList()
                })
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateSubscription(SubscriptionData subscriptionData, CancellationToken cancellationToken)
    {
        CrontabSchedule.Parse(subscriptionData.CronExpression);

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

        SubscriptionValidator.ValidateParameters(subscriptionData.Parameters, queryParams);

        var shouldUpdateHangfire = subscription.CronExpression != subscriptionData.CronExpression;

        subscription.CronExpression = subscriptionData.CronExpression;
        subscription.RecipientId = subscriptionData.RecipientId;

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
                RecipientName = x.Recipient.Name,
                NotificationType = x.Recipient.NotificationType,
                RecipientDestination = x.Recipient.Destination,
                QueryName = x.Query.Name,
                CronExpression = x.CronExpression,
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
}