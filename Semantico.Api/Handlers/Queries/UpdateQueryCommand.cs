using MediatR;
using Microsoft.EntityFrameworkCore;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Validators;

namespace Semantico.Api.Handlers.Queries;

public class UpdateQueryCommand : IRequestHandler<UpdateQueryRequest, UpdateQueryResponse>
{
    private readonly SemanticoContext _context;

    public UpdateQueryCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<UpdateQueryResponse> Handle(UpdateQueryRequest request, CancellationToken cancellationToken)
    {
        var query = await _context.Queries
            .Where(x => x.Id == request.QueryId)
            .FirstAsync(cancellationToken);

        CrontabSchedule.Parse(request.CronExpression);

        QueryValidator.ContainsFlaggedWords(request.SqlValue);

        query.SqlValue = request.SqlValue;
        query.CronExpression = request.CronExpression;

        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class UpdateQueryRequest : IRequest<UpdateQueryResponse>
{
    public int QueryId { get; init; }

    public string SqlValue { get; init; } = string.Empty;

    public string CronExpression { get; init; } = string.Empty;
}

public class UpdateQueryResponse
{
}

