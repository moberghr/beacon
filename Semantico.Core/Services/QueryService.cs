using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.Queries;
using Semantico.Core.Validators;

namespace Semantico.Core.Services;

public interface IQueryService
{
    Task<BaseResponse> CreateQueryAsync(QueryData queryData, CancellationToken cancellationToken);

    Task<BaseResponse> UpdateQueryAsync(QueryData queryData, CancellationToken cancellationToken);

    Task DeleteQueryAsync(int queryId, CancellationToken cancellationToken);

    Task<List<QueryData>> GetQueriesAsync(int? queryId, int? projectId, string? sql, CancellationToken cancellationToken);

    Task<QueryDetailsData> GetQueryDetailsAsync(int queryId, CancellationToken cancellationToken);
}

public class QueryDetailsData
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedTime { get; set; }

    public string SqlValue { get; set; }

    public string ProjectName { get; set; }

    public List<QueryParameterData> Parameters { get; set; } = new();

    public List<SubscriptionListData> Subscriptions { get; set; }
}

public class SubscriptionListData
{
    public int SubscriptionId { get; set; }

    public string Name { get; set; }

    public string CronExpression { get; set; }

    public string Recipient { get; set; }

    public NotificationType NotificationType { get; set; }
}

internal class QueryService : IQueryService
{
    private readonly SemanticoContext _context;

    public QueryService(SemanticoContext context)
    {
        _context = context;
    }

    public async Task CreateQueryAsync(QueryData queryData, CancellationToken cancellationToken)
    {
        QueryValidator.CheckForFlaggedWords(queryData.SqlValue);

        QueryValidator.CheckForParameters(queryData.SqlValue, queryData.Parameters);

        var query = new Query
        {
            SqlValue = queryData.SqlValue,
            ProjectId = queryData.ProjectId
        };

        _context.Queries.Add(query);

        foreach (var queryParameter in queryData.Parameters)
        {
            var parameter = new QueryParameter
            {
                QueryId = query.Id,
                Description = queryParameter.Description,
                Name = queryParameter.Name,
                Type = queryParameter.Type,
                Placeholder = queryParameter.Placeholder,
            };

            _context.QueryParameters.Add(parameter);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteQueryAsync(int queryId, CancellationToken cancellationToken)
    {
        var query = await _context.Queries
            .Include(x => x.Parameters)
            .Where(x => x.Id == queryId)
            .SingleAsync(cancellationToken);

        if (query.Subscriptions.Count > 0)
        {
            throw new SemanticoException($"Unable to remove query due to active subscriptions.");
        }

        query.Archive();

        foreach (var param in query.Parameters)
        {
            param.Archive();
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<QueryData>> GetQueriesAsync(int? queryId, int? projectId, string? sql, CancellationToken cancellationToken)
    {
        return await _context.Queries
            .WhereIf(queryId.HasValue, x => x.Id == queryId)
            .WhereIf(!string.IsNullOrWhiteSpace(sql), x => x.SqlValue.Contains(sql!))
            .WhereIf(projectId.HasValue, x => x.ProjectId == projectId)
            .Select(x =>
                new QueryData
                {
                    QueryId = x.Id,
                    SqlValue = x.SqlValue,
                    ProjectId = x.ProjectId,
                    SubscriptionsCount = x.Subscriptions.Count,
                    CreatedTime = x.CreatedTime,
                    Name = x.Name,
                    Description = x.Description,
                    Parameters = x.Parameters.Select(y =>
                        new QueryParameterData
                        {
                            Name = y.Name,
                            Type = y.Type,
                            Description = y.Description,
                            Placeholder = y.Placeholder
                        }).ToList()
                })
            .ToListAsync(cancellationToken);
    }

    public Task<QueryDetailsData> GetQueryDetailsAsync(int queryId, CancellationToken cancellationToken)
    {
        return _context.Queries
            .Where(x => x.Id == queryId)
            .Select(x =>
                new QueryDetailsData
                {
                    Id = x.Id,
                    CreatedTime = x.CreatedTime,
                    SqlValue = x.SqlValue,
                    ProjectName = x.Project.Name,
                    Name = x.Name,
                    Description = x.Description,
                    Parameters = x.Parameters.Select(y =>
                        new QueryParameterData
                        {
                            Name = y.Name,
                            Type = y.Type,
                            Description = y.Description,
                            Placeholder = y.Placeholder
                        }).ToList(),
                    Subscriptions = x.Subscriptions.Select(y =>
                        new SubscriptionListData
                        {
                            SubscriptionId = y.Id,
                            NotificationType = y.NotificationType,
                            Recipient = y.Recipient,
                            CronExpression = y.CronExpression
                        }).ToList()
                }).SingleAsync(cancellationToken);
    }

    public async Task UpdateQueryAsync(QueryData queryData, CancellationToken cancellationToken)
    {
        var query = await _context.Queries
            .Include(query => query.Parameters)
            .Where(x => x.Id == queryData.QueryId)
            .SingleAsync(cancellationToken);

        QueryValidator.CheckForFlaggedWords(queryData.SqlValue);
        QueryValidator.CheckForParameters(queryData.SqlValue, queryData.Parameters);

        query.SqlValue = queryData.SqlValue;

        foreach (var queryParameter in query.Parameters)
        {
            queryParameter.Archive();
        }

        foreach (var queryParameter in queryData.Parameters)
        {
            var queryParam = new QueryParameter
            {
                QueryId = query.Id,
                Type = queryParameter.Type,
                Name = queryParameter.Name,
                Placeholder = queryParameter.Placeholder,
                Description = queryParameter.Description,
            };

            _context.QueryParameters.Add(queryParam);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}