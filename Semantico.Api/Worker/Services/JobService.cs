using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Semantico.Api.Adapters;
using Semantico.Api.Data;
using Dapper;
using Semantico.Api.Data.Enums;
using Semantico.Api.Helpers;
using Semantico.Api.Validators;
using Semantico.Api.Types;
using Semantico.Api.Handlers.Queries;
using Semantico.Api.Handlers.Subscriptions;
using Semantico.Api.Services;
using System.Data.Common;
using System.Data.SqlClient;

namespace Semantico.Api.Worker.Services;

public class JobService : IJobService
{
    private readonly SemanticoContext _context;
    private readonly INotificationService _notificationService;

    public JobService(SemanticoContext context, INotificationService notificationService)
    {
        _context = context;
        _notificationService = notificationService;
    }

    public async Task ExecuteQuery(int subscriptionId)
    {
        var subscription = await _context.Subscriptions
            .Include(x => x.Parameters)
            .Where(x => x.Id == subscriptionId)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Name,
                    x.NotificationType,
                    x.QueryId,
                    x.Recipient,
                    x.CronExpression,
                    Parameters = x.Parameters.Select(y =>
                        new SubscriptionParameterResponseListData
                        {
                            QueryPlaceholder = y.QueryPlaceholder,
                            Value = y.Value
                        }).ToList()
                })
            .SingleAsync();

        var query = await _context.Queries
            .Where(x => x.Id == subscription.QueryId)
            .Select(x =>
                new
                {
                    x.Id,
                    x.SqlValue,
                    Project = new
                    {
                        x.Project.Name,
                        x.Project.ConnectionString,
                        x.Project.DatabaseEngine
                    },
                    Parameters = x.Parameters.Select(y =>
                        new QueryParameterResponseListData
                        {
                            Name = y.Name,
                            Type = y.Type,
                            Description = y.Description,
                            Placeholder = y.Placeholder
                        }).ToList()
                })
            .SingleAsync();

        SubscriptionValidator.ValidateParameters(subscription.Parameters, query.Parameters);

        var sql = QueryHelper.CompileSql(query.SqlValue, subscription.Parameters);

        QueryValidator.CheckForFlaggedWords(sql);

        var queryResult = await GetQueryResultsAsync(query.Project.DatabaseEngine, query.Project.ConnectionString, sql, query.Project.Name);

        var recipientQueryResult = new RecipientQueryResult
        {
            SubscriptionName = subscription.Name,
            Recipient = subscription.Recipient,
            QueryResult = queryResult
        };

        await _notificationService.SendNotificationAsync(subscriptionId, subscription.NotificationType, recipientQueryResult);
    }

    private static async Task<QueryResult> GetQueryResultsAsync(DatabaseEngineType dbEngineType, string connectionString, string sqlQuery, string projectName)
    {
        using var connection = await GetDbConnectionAsync(dbEngineType, connectionString);
        await connection.OpenAsync();

        var results = await connection.QueryAsync<object>(sqlQuery);

        var recordCounter = results.Count();
        var queryResults = results.Take(10).ToList();

        return new QueryResult
        {
            QueryResults = JsonSerializer.Serialize(queryResults),
            TotalRecords = recordCounter,
            ProjectName = projectName,
            SqlQuery = sqlQuery,
        };
    }

    private static async Task<DbConnection> GetDbConnectionAsync(DatabaseEngineType dbEngineType, string connectionString)
    {
        switch (dbEngineType)
        {
            case DatabaseEngineType.PostgreSQL:
                return new NpgsqlConnection(connectionString);

            case DatabaseEngineType.MSSQL:
                return new SqlConnection(connectionString);

            default:
                throw new SemanticoException($"Unsupported database engine.");
        }
    }
}