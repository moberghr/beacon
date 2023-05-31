using Microsoft.EntityFrameworkCore;
using Npgsql;
using Semantico.Api.Adapters.Mail;
using Semantico.Api.Adapters.Teams;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;

namespace Semantico.Api.Worker.Services;

public class JobService : IJobService
{
    private readonly SemanticoContext _context;
    private readonly string _straumurConnectionString = "Host=localhost;Database=Payfac;Username=postgres;Password=";
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
                    x.SqlValue
                })
            .FirstAsync();

        using (var connection = new NpgsqlConnection(_straumurConnectionString))
        {
            connection.Open();
            using (var command = new NpgsqlCommand(query.SqlValue, connection))
            {
                command.ExecuteNonQuery();
            }
        }

        var notification = await _context.Notifications
            .Where(x => x.QueryId == query.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Value,
                    x.NotificationType
                })
            .FirstOrDefaultAsync();

        if (notification != null)
        {
            switch (notification.NotificationType)
            {
                case NotificationType.Email:

                    var sendEmailRequest = new SendEmailRequest
                    {
                        To = notification.Value,
                        Subject = "Notification",
                        Body = $"Query with id: {query.Id} has be executed"
                    };
                    await _mailAdapter.SendMailAsync(sendEmailRequest);
                    break;

                case NotificationType.Teams:

                    var message = $"Query with id: {query.Id} has be executed";
                    await _teamsAdapter.SendTeamsNotificationAsync(message, notification.Value);
                    break;

                default:
                    throw new Exception("Invalid notification type");
            }
        }
    }
}