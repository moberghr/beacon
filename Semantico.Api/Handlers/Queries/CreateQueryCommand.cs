using Hangfire;
using MediatR;
using NCrontab;
using Semantico.Api.Adapter.Mail;
using Semantico.Api.Adapter.Teams;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Worker.Services;

namespace Semantico.Api.Handlers.Queries;

public class CreateQueryCommand : IRequestHandler<CreateQueryRequest, CreateQueryResponse>
{
    private readonly SemanticoContext _context;
    private readonly IMailAdapter _mailAdapter;
    private readonly ITeamsAdapter _teamsAdapter;

    public CreateQueryCommand(SemanticoContext context, IMailAdapter mailAdapter, ITeamsAdapter teamsAdapter)
    {
        _context = context;
        _mailAdapter = mailAdapter;
        _teamsAdapter = teamsAdapter;
    }

    public async Task<CreateQueryResponse> Handle(CreateQueryRequest request, CancellationToken cancellationToken)
    {
        CrontabSchedule.Parse(request.CronExpression);

        var query = new Query
        {
            SqlValue = request.SqlValue,
            CronExpression = request.CronExpression,
            ProjectId = request.ProjectId
        };

        _context.Queries.Add(query);
        await _context.SaveChangesAsync(cancellationToken);

        var jobService = new JobService(_context, _mailAdapter, _teamsAdapter);
        RecurringJob.AddOrUpdate(query.Id.ToString(), () => jobService.ExecuteQuery(query.Id), query.CronExpression);

        return new();
    }
}

public class CreateQueryRequest : IRequest<CreateQueryResponse>
{
    public string SqlValue { get; init; } = string.Empty;

    public string CronExpression { get; init; } = string.Empty;

    public int ProjectId { get; init; }
}

public class CreateQueryResponse
{
}