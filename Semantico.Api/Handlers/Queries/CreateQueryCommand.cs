using MediatR;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;

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
        var query = new Query
        {
            SqlValue = request.SqlValue,
            CronExpression = request.CronExpression,
            ProjectId = request.ProjectId
        };

        _context.Queries.Add(query);
        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class CreateQueryRequest : IRequest<CreateQueryResponse>
{
    public string SqlValue { get; set; } = string.Empty;

    public string CronExpression { get; set; } = string.Empty;

    public int ProjectId { get; set; }
}

public class CreateQueryResponse
{
}

