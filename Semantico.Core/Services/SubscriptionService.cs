using Microsoft.EntityFrameworkCore;
using NCrontab;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Subscriptions;
using Semantico.Core.Validators;
using Semantico.Core.Worker;

namespace Semantico.Core.Services
{
    public interface ISubscriptionService
    {
        Task CreateSubscriptionAsync(SubscriptionData subscriptionData, CancellationToken cancellationToken);

        Task UpdateSubscriptionAsync(SubscriptionData subscriptionData, CancellationToken cancellationToken);

        Task DeleteSubscriptionAsync(int subscriptionId, CancellationToken cancellationToken);

        Task<List<SubscriptionData>> GetSubscriptionsAsync(int? subscriptionId, int? queryId, NotificationType? notificationType, CancellationToken cancellationToken);
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

        public async Task CreateSubscriptionAsync(SubscriptionData subscriptionData, CancellationToken cancellationToken)
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
                Name = subscriptionData.Name,
                CronExpression = subscriptionData.CronExpression,
                QueryId = subscriptionData.QueryId,
                Recipient = subscriptionData.Recipient,
                NotificationType = subscriptionData.NotificationType
            };

            _context.Subscriptions.Add(subscription);

            foreach (var subscriptionParameter in subscriptionData.Parameters)
            {
                var parameter = new SubscriptionParameter
                {
                    SubscriptionId = subscription.Id,
                    QueryPlaceholder = subscriptionParameter.QueryPlaceholder,
                    Value = subscriptionParameter.Value,
                };

                _context.SubscriptionParameters.Add(parameter);
            }

            await _context.SaveChangesAsync(cancellationToken);


            _semanticoScheduler.AddOrUpdate(subscription.Id, subscription.Name, subscription.CronExpression);
        }

        public async Task DeleteSubscriptionAsync(int subscriptionId, CancellationToken cancellationToken)
        {
            var subscription = await _context.Subscriptions
                .Include(x => x.Parameters)
                .Where(x => x.Id == subscriptionId)
                .SingleAsync(cancellationToken);

            subscription.Archive();

            _semanticoScheduler.Remove(subscription.Id, subscription.Name);

            foreach (var param in subscription.Parameters)
            {
                param.Archive();
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task<List<SubscriptionData>> GetSubscriptionsAsync(int? subscriptionId, int? queryId, NotificationType? notificationType, CancellationToken cancellationToken)
        {
            return await _context.Subscriptions
                .WhereIf(subscriptionId.HasValue, x => x.Id == subscriptionId)
                .WhereIf(queryId.HasValue, x => x.QueryId == queryId)
                .WhereIf(notificationType.HasValue, x => x.NotificationType == notificationType)
                .Select(x =>
                    new SubscriptionData
                    {
                        SubscriptionId = x.Id,
                        Name = x.Name,
                        QueryId = x.QueryId,
                        Recipient = x.Recipient,
                        NotificationType = x.NotificationType,
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

        public async Task UpdateSubscriptionAsync(SubscriptionData subscriptionData, CancellationToken cancellationToken)
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

            subscription.Name = subscriptionData.Name;
            subscription.CronExpression = subscriptionData.CronExpression;
            subscription.Recipient = subscriptionData.Recipient;
            subscription.NotificationType = subscriptionData.NotificationType;

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

            if (shouldUpdateHangfire)
            {
                _semanticoScheduler.AddOrUpdate(subscription.Id, subscription.Name, subscription.CronExpression);
            }
        }
    }
}
