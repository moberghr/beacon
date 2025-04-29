using System.Data.Common;
using System.Data.SqlClient;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Npgsql;
using Semantico.Core.Adapters;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Helpers.File;
using Semantico.Core.Models;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Recipients;
using Semantico.Core.Models.Subscriptions;
using Semantico.Core.Validators;

namespace Semantico.Core.Services;

public interface IQueryService
{
    Task<BaseResponse> CreateQuery(QueryData queryData, CancellationToken cancellationToken);

    Task<BaseResponse> UpdateQuery(QueryData queryData, CancellationToken cancellationToken);

    Task DeleteQuery(int queryId, CancellationToken cancellationToken);

    Task<PagedList<QueryData>> GetQueries(GetQueriesRequest request, CancellationToken cancellationToken);

    Task<QueryDetailsData> GetQueryDetails(int queryId, CancellationToken cancellationToken);
    
    Task<QueryResult> ExecuteQuery(int subscriptionId, CancellationToken cancellationToken);
}

public class QueryDetailsData
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedTime { get; set; }

    public string SqlValue { get; set; }

    public string ProjectName { get; set; }

    public int TotalExecutions { get; set; }

    public int SentNotifications { get; set; }

    public List<QueryParameterData> Parameters { get; set; } = new();

    public List<SubscriptionListData> Subscriptions { get; set; } = new();
}

public class SubscriptionListData
{
    public int SubscriptionId { get; set; }

    public string Name { get; set; }

    public string CronExpression { get; set; }
}

public class GetQueriesRequest : SortedListRequest
{
    public int? QueryId { get; set; }
    public int? ProjectId { get; set; }
}

internal class QueryService : IQueryService
{
    private readonly SemanticoContext _context;

    public QueryService(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<BaseResponse> CreateQuery(QueryData queryData, CancellationToken cancellationToken)
    {
        QueryValidator.CheckForFlaggedWords(queryData.SqlValue);

        QueryValidator.CheckForParameters(queryData.SqlValue, queryData.Parameters);

        var query = new Query
        {
            SqlValue = queryData.SqlValue,
            ProjectId = queryData.ProjectId,
            Name = queryData.Name,
            Description = queryData.Description
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

        return new BaseResponse
        {
            Message = "Saved successfuly",
            Success = true
        };
    }

    public async Task DeleteQuery(int queryId, CancellationToken cancellationToken)
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

    public async Task<PagedList<QueryData>> GetQueries(GetQueriesRequest request, CancellationToken cancellationToken)
    {
        return await _context.Queries
            .WhereIf(request.QueryId.HasValue, x => x.Id == request.QueryId)
            .WhereIf(request.ProjectId.HasValue, x => x.ProjectId == request.ProjectId)
            .Select(x =>
                new QueryData
                {
                    QueryId = x.Id,
                    SqlValue = x.SqlValue,
                    ProjectId = x.ProjectId,
                    ProjectName = x.Project.Name,
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
            .ToPagedListAsync(request, cancellationToken);
    }

    public Task<QueryDetailsData> GetQueryDetails(int queryId, CancellationToken cancellationToken)
    {
        return _context.Queries
            .AsSplitQuery()
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
                    TotalExecutions = x.Subscriptions.Sum(y => y.QueryExecutionHistory.Count),
                    SentNotifications = x.Subscriptions.Sum(y => y.QueryExecutionHistory.Count(z => z.NotificationStatus == NotificationStatus.NotificationSent)),
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
                            Name = y.Query.Name,
                            CronExpression = y.CronExpression
                        }).ToList()
                }).SingleAsync(cancellationToken);
    }

    public async Task<QueryResult> ExecuteQuery(int subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await _context.Subscriptions
            .AsSplitQuery()
            .Where(x => x.Id == subscriptionId)
            .Select(x =>
                new
                {
                    x.Id,
                    Recipients = x.Recipients.Select(y => new RecipientData
                    {
                        RecipientId = y.Id,
                        Name = y.Name,
                        Description = y.Description,
                        Destination = y.Destination,
                        NotificationType = y.NotificationType,
                        ResultAttachmentType = y.ResultAttachmentType,
                    }).ToList(),
                    x.QueryId,
                    x.CronExpression,
                    x.TimeoutSeconds,
                    x.Query.SqlValue,
                    x.Query.Name,
                    Project = new
                    {
                        x.Query.Project.Name,
                        x.Query.Project.ConnectionString,
                        x.Query.Project.DatabaseEngineType
                    },
                    Parameters = x.Parameters.Select(y =>
                        new SubscriptionParamaterData
                        {
                            QueryPlaceholder = y.QueryPlaceholder,
                            Value = y.Value
                        }).ToList()
                })
            .SingleAsync(cancellationToken);
        
        var sql = QueryHelper.CompileSql(subscription.SqlValue, subscription.Parameters);

        QueryValidator.CheckForFlaggedWords(sql);

        var (results, executionTimeMs) = await ExecuteQueryAsync(
            subscription.Project.DatabaseEngineType, 
            subscription.Project.ConnectionString, 
            sql,
            subscription.TimeoutSeconds);

        // We will only send the top 10 rows in a notification.
        var messageRows = results.Take(10).ToList();

        var queryResult = new QueryResult
        {
            QueryResults = JsonSerializer.Serialize(messageRows),
            TopRecords = messageRows,
            TotalRecords = results.Count,
            ProjectName = subscription.Project.Name,
            SqlQuery = sql,
            Recipients = subscription.Recipients,
            SubscriptionName = subscription.Name,
            AllRecords = results,
            ExecutionTimeMs = executionTimeMs
        };

        return queryResult;
    }

    public async Task<BaseResponse> UpdateQuery(QueryData queryData, CancellationToken cancellationToken)
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

        return new BaseResponse
        {
            Message = "Saved successfuly",
            Success = true
        };
    }
    
    private async Task<(List<IDictionary<string, object?>> Results, double ExecutionTimeMs)> ExecuteQueryAsync(
        DatabaseEngineType dbEngineType, 
        string connectionString, 
        string sqlQuery, 
        int? timeoutSeconds = null)
    {
        await using var connection = GetDbConnection(dbEngineType, connectionString);
        await connection.OpenAsync();

        // Replace newline, carriage return, and tab characters with a space
        var cleanedSql = sqlQuery.Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Trim();

        var stopwatch = new System.Diagnostics.Stopwatch();
        stopwatch.Start();
        
        try
        {
            // Create a cancellation token source with timeout if specified
            using var timeoutCts = timeoutSeconds.HasValue 
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value)) 
                : new CancellationTokenSource();
            
            // Combine with the provided cancellation token if needed
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
            
            // Execute the query with the timeout
            var commandDefinition = new CommandDefinition(
                commandText: cleanedSql,
                commandTimeout: timeoutSeconds,
                cancellationToken: linkedCts.Token
            );
            
            var dapperRows = await connection.QueryAsync(commandDefinition);
            
            stopwatch.Stop();
            var executionTimeMs = stopwatch.Elapsed.TotalMilliseconds;

            // Convert the dapper rows to a list of dictionaries for reflection and serialization
            var results = dapperRows.Select(x => (IDictionary<string, object?>)x).ToList();
            return (results, executionTimeMs);
        }
        catch (TaskCanceledException)
        {
            // Query was cancelled due to timeout
            stopwatch.Stop();
            var executionTimeMs = stopwatch.Elapsed.TotalMilliseconds;
            
            // Return empty results for timeout
            return (new List<IDictionary<string, object?>>(), executionTimeMs);
        }
        catch (Exception)
        {
            stopwatch.Stop();
            // Re-throw other exceptions
            throw;
        }
    }

    private static DbConnection GetDbConnection(DatabaseEngineType dbEngineType, string connectionString) => dbEngineType switch
    {
        DatabaseEngineType.PostgreSQL => new NpgsqlConnection(connectionString),
        DatabaseEngineType.MSSQL => new SqlConnection(connectionString),
        DatabaseEngineType.MySQL => new MySqlConnection(connectionString),
        _ => throw new SemanticoException($"Unsupported database engine.")
    };
}