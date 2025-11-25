using Microsoft.EntityFrameworkCore;
using Cronos;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
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

internal class SubscriptionService(IDbContextFactory<SemanticoContext> contextFactory, ISemanticoScheduler semanticoScheduler)
    : ISubscriptionService
{
    public async Task<BaseResponse> CreateSubscription(SubscriptionData subscriptionData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        CronExpression.Parse(subscriptionData.CronExpression);

        var queryParams = await context.QueryParameters
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

        // Validate recipients: required unless CreateTasks is enabled
        if (!subscriptionData.CreateTasks && (subscriptionData.Recipients == null || !subscriptionData.Recipients.Any()))
        {
            throw new SemanticoException("At least one recipient is required when 'Create Tasks' is not enabled");
        }

        var recipients = await context.Recipients
            .Where(x => subscriptionData.Recipients.Select(y => y.RecipientId).Contains(x.Id))
            .ToListAsync(cancellationToken);

        var subscription = new Subscription
        {
            CronExpression = subscriptionData.CronExpression,
            QueryId = subscriptionData.QueryId,
            MaxRows = subscriptionData.MaxRows,
            IncludeAttachment = subscriptionData.IncludeAttachment,
            ResultAttachmentType = subscriptionData.ResultAttachmentType,
            ShowQuery = subscriptionData.ShowQuery,
            TimeoutSeconds = subscriptionData.TimeoutSeconds,
            StoreResults = subscriptionData.StoreResults,
            CreateTasks = subscriptionData.CreateTasks,
            Recipients = recipients,
            Parameters = subscriptionData.Parameters.Select(x =>
                new SubscriptionParameter
                {
                    QueryPlaceholder = x.QueryPlaceholder,
                    Value = x.Value,
                }).ToList()
        };

        context.Subscriptions.Add(subscription);

        await context.SaveChangesAsync(cancellationToken);

        var query = context.Queries.Single(x => x.Id == subscriptionData.QueryId);

        semanticoScheduler.AddOrUpdate(subscription.Id, $"{query.Name}: {subscription.Id}", subscription.CronExpression);

        return new BaseResponse { Success = true, Message = "Subscription created successfully" };
    }

    public async Task DeleteSubscription(int subscriptionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var subscription = await context.Subscriptions
            .Include(x => x.Parameters)
            .Include(x => x.Query)
            .Where(x => x.Id == subscriptionId)
            .SingleAsync(cancellationToken);

        subscription.Archive();

        semanticoScheduler.Remove(subscription.Id, $"{subscription.Query.Name}: {subscription.Id}");

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
                        NotificationType = y.NotificationType
                    }).ToList(),
                    CronExpression = x.CronExpression,
                    MaxRows = x.MaxRows,
                    IncludeAttachment = x.IncludeAttachment,
                    ResultAttachmentType = x.ResultAttachmentType,
                    ShowQuery = x.ShowQuery,
                    TimeoutSeconds = x.TimeoutSeconds,
                    StoreResults = x.StoreResults,
                    CreateTasks = x.CreateTasks,
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
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        CronExpression.Parse(subscriptionData.CronExpression);

        var subscription = await context.Subscriptions
            .Include(subscription => subscription.Parameters)
            .Where(x => x.Id == subscriptionData.SubscriptionId)
            .SingleAsync(cancellationToken);

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
            throw new SemanticoException("At least one recipient is required when 'Create Tasks' is not enabled");
        }

        var recipients = await context.Recipients
            .Where(x => subscriptionData.Recipients.Select(y => y.RecipientId).Contains(x.Id))
            .ToListAsync(cancellationToken);

        var shouldUpdateHangfire = subscription.CronExpression != subscriptionData.CronExpression;

        subscription.CronExpression = subscriptionData.CronExpression;
        subscription.MaxRows = subscriptionData.MaxRows;
        subscription.IncludeAttachment = subscriptionData.IncludeAttachment;
        subscription.ResultAttachmentType = subscriptionData.ResultAttachmentType;
        subscription.ShowQuery = subscriptionData.ShowQuery;
        subscription.TimeoutSeconds = subscriptionData.TimeoutSeconds;
        subscription.StoreResults = subscriptionData.StoreResults;
        subscription.CreateTasks = subscriptionData.CreateTasks;
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

            context.SubscriptionParameters.Add(subscriptionParam);
        }

        await context.SaveChangesAsync(cancellationToken);

        var query = context.Queries.Single(x => x.Id == subscription.QueryId);

        if (shouldUpdateHangfire)
        {
            semanticoScheduler.AddOrUpdate(subscription.Id, $"{query.Name}: {subscription.Id}", subscription.CronExpression);
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
                CronExpression = x.CronExpression,
                MaxRows = x.MaxRows,
                IncludeAttachment = x.IncludeAttachment,
                ResultAttachmentType = x.ResultAttachmentType,
                ShowQuery = x.ShowQuery,
                TimeoutSeconds = x.TimeoutSeconds,
                StoreResults = x.StoreResults,
                CreateTasks = x.CreateTasks,
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
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .Include(x => x.Recipients)
            .SingleAsync(cancellationToken);

        subscription.Recipients = subscription.Recipients.Where(x => x.Id != recipientId).ToList();

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRecipients(int subscriptionId, List<int> recipientIds, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .Include(x => x.Recipients)
            .SingleAsync(cancellationToken);

        recipientIds = recipientIds.Except(subscription.Recipients.Select(x => x.Id)).ToList();

        var recipients = await context.Recipients
            .Where(x => recipientIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        subscription.Recipients.AddRange(recipients);

        await context.SaveChangesAsync(cancellationToken);
    }
}