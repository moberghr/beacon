using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.AI.Services.Ai.DocumentationAgent;
using Semantico.Core.Handlers.Ai.DocumentationAgent;

namespace Semantico.AI.Handlers.Ai.DocumentationAgent;

internal sealed class CancelDocumentationAgentHandler
    : IRequestHandler<CancelDocumentationAgentCommand>
{
    private readonly IDocumentationAgentService _agentService;
    private readonly ILogger<CancelDocumentationAgentHandler> _logger;

    public CancelDocumentationAgentHandler(
        IDocumentationAgentService agentService,
        ILogger<CancelDocumentationAgentHandler> logger)
    {
        _agentService = agentService;
        _logger = logger;
    }

    public async Task Handle(
        CancelDocumentationAgentCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cancelling documentation agent run {AgentRunId}", request.AgentRunId);

        await _agentService.CancelDocumentationAsync(request.AgentRunId, cancellationToken);

        _logger.LogInformation("Documentation agent run {AgentRunId} cancelled", request.AgentRunId);
    }
}

