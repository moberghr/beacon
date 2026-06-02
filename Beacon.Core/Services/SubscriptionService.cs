using Microsoft.EntityFrameworkCore;
using Cronos;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Validators;
using Beacon.Core.Worker;

namespace Beacon.Core.Services;

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

internal class SubscriptionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconScheduler beaconScheduler,
    IAnomalyDetectionService anomalyDetectionService)
    : ISubscriptionService
{
    public async Task<BaseResponse> CreateSubscription(SubscriptionData subscriptionData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        CronExpression.Parse(subscriptionData.CronExpression);

        var queryParams = await context.QuerySteps
            .Where(qs => qs.QueryId == subscriptionData.QueryId)
            .SelectMany(qs => qs.Parameters)
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

        // Validate recipients: required unless CreateTasks is enabled
        if (!subscriptionData.CreateTasks && (subscriptionData.Recipients == null || !subscriptionData.Recipients.Any()))
        {
            throw new BeaconException("At least one recipient is required when 'Create Tasks' is not enabled");
        }

        var recipients = await context.Recipients
            .Where(x => subscriptionData.Recipients.Select(y => y.RecipientId).Contains(x.Id))
            .ToListAsync(cancellationToken);

        var subscription = new Subscription
        {
            CronExpression = subscriptionData.CronExpression,
            QueryId = subscriptionData.QueryId,
            MaxRows = subscriptionData.MaxRows,
            MinimumRowCount = subscriptionData.MinimumRowCount,
            IncludeAttachment = subscriptionData.IncludeAttachment,
            ResultAttachmentType = subscriptionData.ResultAttachmentType,
            ShowQuery = subscriptionData.ShowQuery,
            TimeoutSeconds = subscriptionData.TimeoutSeconds,
            StoreResults = subscriptionData.StoreResults,
            CreateTasks = subscriptionData.CreateTasks,
            NotificationTrigger = subscriptionData.NotificationTrigger,
            Recipients = recipients,
            Parameters = ParameterEntityFactory.CreateSubscriptionParameters(subscriptionData.Parameters)
        };

        context.Subscriptions.Add(subscription);

        await context.SaveChangesAsync(cancellationToken);

        // Save anomaly configuration if provided
        if (subscriptionData.AnomalyConfig != null)
        {
            subscriptionData.AnomalyConfig.SubscriptionId = subscription.Id;
            await anomalyDetectionService.SaveAnomalyConfigAsync(subscriptionData.AnomalyConfig, cancellationToken);
        }

        var query = context.Queries
            .Where(x => x.Id == subscriptionData.QueryId)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Query {subscriptionData.QueryId} not found.");

        beaconScheduler.AddOrUpdate(subscription.Id, $"{query.Name}: {subscription.Id}", subscription.CronExpression);

        return new BaseResponse { Success = true, Message = "Subscription created successfully" };
    }

    public async Task DeleteSubscription(int subscriptionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var subscription = await context.Subscriptions
            .Include(x => x.Parameters)
            .Include(x => x.Query)
            .Where(x => x.Id == subscriptionId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found.");

        subscription.Archive();

        beaconScheduler.Remove(subscription.Id, $"{subscription.Query.Name}: {subscription.Id}");

        foreach (var param in subscription.Parameters)
        {
            param.Archive();
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<SubscriptionData>> GetSubscriptions(int? subscriptionId, int? queryId, NotificationType? notificationType, string? keyword, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Subscriptions
            .WhereIf(subscriptionId.HasValue, x => x.Id == subscriptionId)
            .WhereIf(queryId.HasValue, x => x.QueryId == queryId)
            .WhereIf(!string.IsNullOrWhiteSpace(keyword), x => x.Query.Name.Contains(keyword!))
            .Select(x =>
                new SubscriptionData
                {
                    SubscriptionId = x.Id,
                    QueryId = x.QueryId,
                    QueryName = x.Query.Name,
                    AiActorId = x.AiActorId,
                    AiActorName = x.AiActor != null ? x.AiActor.Name : null,
                    Recipients = x.Recipients.Select(y => new RecipientData
                    {
                        RecipientId = y.Id,
                        Name = y.Name,
                        Description = y.Description,
                        Destination = y.Destination,
                        NotificationType = y.NotificationType
                    }).ToList(),
                    CronExpression = x.CronExpression,
                    MaxRows = x.MaxRows,
                    MinimumRowCount = x.MinimumRowCount,
                    IncludeAttachment = x.IncludeAttachment,
                    ResultAttachmentType = x.ResultAttachmentType,
                    ShowQuery = x.ShowQuery,
                    TimeoutSeconds = x.TimeoutSeconds,
                    StoreResults = x.StoreResults,
                    CreateTasks = x.CreateTasks,
                    Parameters = x.Parameters.Select(y => new SubscriptionParameterData
                    {
                        QueryPlaceholder = y.QueryPlaceholder,
                        Value = y.Value
                    }).ToList()
                })
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateSubscription(SubscriptionData subscriptionData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        CronExpression.Parse(subscriptionData.CronExpression);

        var subscription = await context.Subscriptions
            .Include(x => x.Parameters)
            .Where(x => x.Id == subscriptionData.SubscriptionId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Subscription {subscriptionData.SubscriptionId} not found.");

        var queryParams = await context.QueryParameters
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

        // Validate recipients: required unless CreateTasks is enabled
        if (!subscriptionData.CreateTasks && (subscriptionData.Recipients == null || !subscriptionData.Recipients.Any()))
        {
            throw new BeaconException("At least one recipient is required when 'Create Tasks' is not enabled");
        }

        var recipients = await context.Recipients
            .Where(x => subscriptionData.Recipients.Select(y => y.RecipientId).Contains(x.Id))
            .ToListAsync(cancellationToken);

        var shouldUpdateHangfire = subscription.CronExpression != subscriptionData.CronExpression;

        subscription.CronExpression = subscriptionData.CronExpression;
        subscription.MaxRows = subscriptionData.MaxRows;
        subscription.MinimumRowCount = subscriptionData.MinimumRowCount;
        subscription.IncludeAttachment = subscriptionData.IncludeAttachment;
        subscription.ResultAttachmentType = subscriptionData.ResultAttachmentType;
        subscription.ShowQuery = subscriptionData.ShowQuery;
        subscription.TimeoutSeconds = subscriptionData.TimeoutSeconds;
        subscription.StoreResults = subscriptionData.StoreResults;
        subscription.CreateTasks = subscriptionData.CreateTasks;
        subscription.NotificationTrigger = subscriptionData.NotificationTrigger;
        subscription.Recipients = recipients;

        foreach (var subscriptionParameter in subscription.Parameters)
        {
            subscriptionParameter.Archive();
        }

        var newParams = ParameterEntityFactory.CreateSubscriptionParameters(subscriptionData.Parameters, subscription.Id);
        context.SubscriptionParameters.AddRange(newParams);

        await context.SaveChangesAsync(cancellationToken);

        // Save anomaly configuration if provided
        if (subscriptionData.AnomalyConfig != null)
        {
            subscriptionData.AnomalyConfig.SubscriptionId = subscription.Id;
            await anomalyDetectionService.SaveAnomalyConfigAsync(subscriptionData.AnomalyConfig, cancellationToken);
        }

        var query = context.Queries
            .Where(x => x.Id == subscription.QueryId)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Query {subscription.QueryId} not found.");

        if (shouldUpdateHangfire)
        {
            beaconScheduler.AddOrUpdate(subscription.Id, $"{query.Name}: {subscription.Id}", subscription.CronExpression);
        }
    }

    public async Task<SubscriptionDetailsData> GetSubscriptionDetails(int subscriptionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var subscription = await context.Subscriptions
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
                    NotificationType = y.NotificationType
                }).ToList(),
                QueryName = x.Query.Name,
                AiActorId = x.AiActorId,
                AiActorName = x.AiActor != null ? x.AiActor.Name : null,
                CronExpression = x.CronExpression,
                MaxRows = x.MaxRows,
                MinimumRowCount = x.MinimumRowCount,
                IncludeAttachment = x.IncludeAttachment,
                ResultAttachmentType = x.ResultAttachmentType,
                ShowQuery = x.ShowQuery,
                TimeoutSeconds = x.TimeoutSeconds,
                StoreResults = x.StoreResults,
                CreateTasks = x.CreateTasks,
                NotificationTrigger = x.NotificationTrigger,
                Status = x.ArchivedTime.HasValue ? "Archived" : "Active",
                Parameters = x.Parameters.Select(y => new SubscriptionParameterData()
                {
                    QueryPlaceholder = y.QueryPlaceholder,
                    Value = y.Value
                }).ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found.");

        // Load anomaly configuration if it exists
        subscription.AnomalyConfig = await anomalyDetectionService.GetAnomalyConfigAsync(subscriptionId, cancellationToken);

        return subscription;
    }

    public async Task RemoveRecipient(int subscriptionId, int recipientId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .Include(x => x.Recipients)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found.");

        subscription.Recipients = subscription.Recipients.Where(x => x.Id != recipientId).ToList();

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRecipients(int subscriptionId, List<int> recipientIds, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .Include(x => x.Recipients)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found.");

        recipientIds = recipientIds.Except(subscription.Recipients.Select(x => x.Id)).ToList();

        var recipients = await context.Recipients
            .Where(x => recipientIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        subscription.Recipients.AddRange(recipients);

        await context.SaveChangesAsync(cancellationToken);
    }
}