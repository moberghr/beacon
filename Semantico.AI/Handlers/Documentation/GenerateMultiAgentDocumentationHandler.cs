using MediatR;
using Semantico.AI.Models.MultiAgent;
using Semantico.AI.Services.Ai.MultiAgent;
using Semantico.Core.Data.Entities;
using Semantico.Core.Handlers.Ai.DocumentationAgent;

namespace Semantico.AI.Handlers.Documentation;

internal sealed class GenerateMultiAgentDocumentationHandler(
    IMultiAgentDocumentationService multiAgentService)
    : IRequestHandler<GenerateMultiAgentDocumentationCommand, DataSourceDocumentation>
{
    public async Task<DataSourceDocumentation> Handle(
        GenerateMultiAgentDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        var options = new MultiAgentGenerationOptions
        {
            MaxConcurrentAgents = request.MaxConcurrentAgents ?? 5,
            MinTablesPerDomain = 3,
            MaxDomainsToIdentify = 7,
            Temperature = 0.3m,
            EnableOrchestratorCache = true,
            OrchestratorCacheDurationMinutes = 60,
            SpecificTables = request.SpecificTables,
            ExcludedTables = request.ExcludedTables,
            MaxTables = request.MaxTables ?? 200,
            Title = request.Title,
            MaxTokens = 4096
        };

        return await multiAgentService.GenerateDocumentationAsync(
            request.DataSourceId,
            request.UserId,
            options,
            request.Progress,
            cancellationToken);
    }
}

