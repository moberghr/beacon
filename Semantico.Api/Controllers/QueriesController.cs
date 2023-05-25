using MediatR;
using Microsoft.AspNetCore.Mvc;
using Semantico.Api.Handlers.Queries;

namespace Semantico.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class QueriesController : ControllerBase
{
    private readonly IMediator _mediator;

    public QueriesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<GetQueriesResponse> GetQueries([FromQuery] GetQueriesRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpPost]
    public async Task<CreateQueryResponse> CreateQuery(CreateQueryRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpPut]
    public async Task<UpdateQueryResponse> UpdateQuery(UpdateQueryRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }

    [HttpDelete]
    public async Task<DeleteQueryResponse> DeleteQuery([FromQuery] DeleteQueryRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }
}