using MediatR;
using Microsoft.AspNetCore.Mvc;
using Semantico.Api.Handlers.Notifications;

namespace Semantico.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class QueryExecutionHistoryController : ControllerBase
{
    private readonly IMediator _mediator;

    public QueryExecutionHistoryController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<GetQueryExecutionHistoryResponse> GetQueryExecutionHistory([FromQuery] GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken)
    {
        return await _mediator.Send(request, cancellationToken);
    }
}