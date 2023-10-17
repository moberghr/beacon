using MediatR;
using NCrontab;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Validators;
using Semantico.Api.Worker;
using Semantico.Api.Worker.Services;

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
        QueryValidator.ContainsFlaggedWords(request.SqlValue);

        var query = new Query
        {
            SqlValue = request.SqlValue,
            ProjectId = request.ProjectId
        };

        _context.Queries.Add(query);
        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class CreateQueryRequest : IRequest<CreateQueryResponse>
{
    public string SqlValue { get; init; } = string.Empty;

    public int ProjectId { get; init; }
}

public class CreateQueryResponse
{
}