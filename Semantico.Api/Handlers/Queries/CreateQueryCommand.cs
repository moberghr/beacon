using Hangfire;
using MediatR;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Worker;

namespace Semantico.Api.Handlers.Queries;

public class CreateQueryCommand : IRequestHandler<CreateQueryRequest, CreateQueryResponse>
{
    private readonly SemanticoContext _context;

    public CreateQueryCommand(SemanticoContext context)
    {
        _context = context;
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

        RecurringJob.AddOrUpdate<IJobService>(query.Id.ToString(), x => x.ExecuteQuery(query.Id), query.CronExpression);

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