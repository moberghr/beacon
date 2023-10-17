using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Semantico.Api.Adapters;
using Semantico.Api.Adapters.Mail;
using Semantico.Api.Adapters.Teams;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Dapper;

namespace Semantico.Api.Worker.Services;

public class JobService : IJobService
{
    private readonly SemanticoContext _context;
    private readonly IMailAdapter _mailAdapter;
    private readonly ITeamsAdapter _teamsAdapter;

    public JobService(SemanticoContext context, IMailAdapter mailAdapter, ITeamsAdapter teamsAdapter)
    {
        _context = context;
        _mailAdapter = mailAdapter;
        _teamsAdapter = teamsAdapter;
    }

    public async Task ExecuteQuery(int queryId)
    {
        var query = await _context.Queries
            .Where(x => x.Id == queryId)
            .Select(x =>
                new
                {
                    x.Id,
                    x.SqlValue,
                    x.Project,
                    x.Subscriptions
                })
            .FirstAsync();

        var queryResult = await GetQueryResults(query.Project.ConnectionString, query.SqlValue, query.Project.Name);

        // TODO: implement notification sending logic

        //foreach (var subscription in query.Subscriptions)
        //{
        //    var recipientQueryResult = new RecipientQueryResult
        //    {
        //        Recipient = subscription.Value,
        //        QueryResult = queryResult
        //    };

        //    switch (subscription.NotificationType)
        //    {
        //        case NotificationType.Email:
        //            await _mailAdapter.SendMailAsync(recipientQueryResult);
        //            break;

        //        case NotificationType.Teams:
        //            await _teamsAdapter.SendTeamsNotificationAsync(recipientQueryResult);
        //            break;

        //        default:
        //            throw new Exception("Invalid notification type");
        //    }
        //}
    }

    private static async Task<QueryResult> GetQueryResults(string connectionString, string sqlQuery, string projectName)
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var results = await connection.QueryAsync<object>(sqlQuery);

        var recordCounter = results.Count();
        var queryResults = results.Take(10).ToList();

        return new QueryResult
        {
            QueryResults = JsonSerializer.Serialize(queryResults),
            TotalRecords = recordCounter,
            ProjectName = $"{projectName} - notification",
            SqlQuery = sqlQuery,
        };
    }
}