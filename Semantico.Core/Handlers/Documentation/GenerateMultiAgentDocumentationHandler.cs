using MediatR;
using Semantico.Core.Data.Entities;
using Semantico.Core.Models.Ai.MultiAgent;
using Semantico.Core.Services.Ai.MultiAgent;

namespace Semantico.Core.Handlers.Documentation;

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

public record GenerateMultiAgentDocumentationCommand : IRequest<DataSourceDocumentation>
{
    public int DataSourceId { get; init; }
    public int UserId { get; init; }
    public int? MaxConcurrentAgents { get; init; }
    public List<string>? SpecificTables { get; init; }
    public List<string>? ExcludedTables { get; init; }
    public int? MaxTables { get; init; }
    public string? Title { get; init; }
    public IProgress<DocumentationProgress>? Progress { get; init; }
}
