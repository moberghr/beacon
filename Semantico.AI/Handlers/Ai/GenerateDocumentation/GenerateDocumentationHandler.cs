using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.AI.Models.Ai;
using Semantico.AI.Services.Ai;
using Semantico.Core.Handlers.Ai.GenerateDocumentation;

namespace Semantico.AI.Handlers.Ai.GenerateDocumentation;

internal sealed class GenerateDocumentationHandler
    : IRequestHandler<GenerateDocumentationCommand, GenerateDocumentationResult>
{
    private readonly IAiDocumentationService _documentationService;
    private readonly ILogger<GenerateDocumentationHandler> _logger;

    public GenerateDocumentationHandler(
        IAiDocumentationService documentationService,
        ILogger<GenerateDocumentationHandler> logger)
    {
        _documentationService = documentationService;
        _logger = logger;
    }

    public async Task<GenerateDocumentationResult> Handle(
        GenerateDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Generating documentation for DataSource {DataSourceId} by User {UserId}",
            request.DataSourceId,
            request.UserId);

        var startTime = DateTime.UtcNow;

        var documentation = await _documentationService.GenerateDocumentationAsync(
            request.DataSourceId,
            request.UserId,
            request.Options,
            cancellationToken);

        var generationTime = DateTime.UtcNow - startTime;

        _logger.LogInformation(
            "Documentation generated successfully: {DocumentationId}",
            documentation.Id);

        return new GenerateDocumentationResult
        {
            DocumentationId = documentation.Id,
            Title = documentation.Title,
            TablesAnalyzed = documentation.TablesAnalyzed,
            SectionsGenerated = documentation.Sections.Count,
            TokensUsed = documentation.TokensUsed,
            EstimatedCost = documentation.EstimatedCost,
            GenerationTime = generationTime,
            GeneratedByModel = documentation.GeneratedByModel,
            Status = documentation.Status,
            Warnings = new List<string>()
        };
    }
}

