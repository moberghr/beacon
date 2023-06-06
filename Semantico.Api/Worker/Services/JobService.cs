using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Semantico.Api.Adapters;
using Semantico.Api.Adapters.Mail;
using Semantico.Api.Adapters.Teams;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;

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
                    x.Notifications
                })
            .FirstAsync();

        var messageRequest = await GetQueryResults(query.Project.ConnectionString, query.SqlValue, query.Project.Name);

        foreach (var notification in query.Notifications)
        {
            messageRequest.Recipient = notification.Value;

            switch (notification.NotificationType)
            {
                case NotificationType.Email:
                    await _mailAdapter.SendMailAsync(messageRequest, notification.Value);
                    break;

                case NotificationType.Teams:
                    await _teamsAdapter.SendTeamsNotificationAsync(messageRequest, notification.Value);
                    break;

                default:
                    throw new Exception("Invalid notification type");
            }
        }
    }

    private static async Task<MessageRequest> GetQueryResults(string connectionString, string sqlQuery, string projectName)
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        using var command = new NpgsqlCommand(sqlQuery, connection);
        using var reader = await command.ExecuteReaderAsync();

        var results = new Dictionary<string, List<string>>();
        var recordCounter = 0;
        var jsonRecordCounter = 0;

        while (await reader.ReadAsync())
        {
            if (jsonRecordCounter < 10)
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var fieldName = reader.GetName(i);
                    var fieldValue = reader[i].ToString();

                    if (!results.ContainsKey(fieldName))
                    {
                        results[fieldName] = new List<string>();
                    }

                    results[fieldName].Add(fieldValue ?? string.Empty);
                }

                jsonRecordCounter++;
            }

            recordCounter++;
        }

        return new MessageRequest
        {
            QueryResults = JsonSerializer.Serialize(results),
            TotalRecords = recordCounter,
            ProjectName = $"{projectName} - notification",
            SqlQuery = sqlQuery,
        };
    }
}