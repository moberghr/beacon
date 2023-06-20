using MediatR;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Worker;
using Semantico.Api.Worker.Services;

namespace Semantico.Api.Handlers.Queries;

public class CreateQueryCommand : IRequestHandler<CreateQueryRequest, CreateQueryResponse>
{
    private readonly SemanticoContext _context;
    private readonly IRecurringJobService _recurringJobService;

    public CreateQueryCommand(SemanticoContext context, IRecurringJobService recurringJobService)
    {
        _context = context;
        _recurringJobService = recurringJobService;
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

        _recurringJobService.AddOrUpdate(query.Id, query.Id.ToString(), query.CronExpression);

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