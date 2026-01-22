using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Entities;
using Semantico.AI.Services.Ai.DocumentationAgent;
using Semantico.AI.Services.Ai.DocumentationAgent.Models;
using Semantico.Core.Handlers.Ai.DocumentationAgent;

namespace Semantico.AI.Handlers.Ai.DocumentationAgent;

internal sealed class StartDocumentationAgentHandler
    : IRequestHandler<StartDocumentationAgentCommand, StartDocumentationAgentResult>
{
    private readonly IDocumentationAgentService _agentService;
    private readonly ILogger<StartDocumentationAgentHandler> _logger;

    public StartDocumentationAgentHandler(
        IDocumentationAgentService agentService,
        ILogger<StartDocumentationAgentHandler> logger)
    {
        _agentService = agentService;
        _logger = logger;
    }

    public async Task<StartDocumentationAgentResult> Handle(
        StartDocumentationAgentCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting documentation agent for DataSource {DataSourceId} by User {UserId}",
            request.DataSourceId,
            request.UserId);

        var agentRun = await _agentService.StartDocumentationAsync(
            request.DataSourceId,
            request.UserId,
            request.Options,
            cancellationToken);

        return new StartDocumentationAgentResult
        {
            AgentRunId = agentRun.Id,
            DocumentationId = agentRun.DocumentationId,
            Status = agentRun.Status,
            Message = "Documentation agent started successfully. Use GetDocumentationAgentProgress to monitor progress."
        };
    }
}

