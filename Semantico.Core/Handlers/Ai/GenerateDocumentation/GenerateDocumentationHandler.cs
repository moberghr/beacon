using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Models.Ai;
using Semantico.Core.Services.Ai;

namespace Semantico.Core.Handlers.Ai.GenerateDocumentation;

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

public record GenerateDocumentationCommand : IRequest<GenerateDocumentationResult>
{
    public int DataSourceId { get; init; }
    public int UserId { get; init; }
    public GenerationOptions Options { get; init; } = new();
}

public record GenerateDocumentationResult
{
    public int DocumentationId { get; init; }
    public string Title { get; init; } = null!;
    public int TablesAnalyzed { get; init; }
    public int SectionsGenerated { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public TimeSpan GenerationTime { get; init; }
    public string GeneratedByModel { get; init; } = null!;
    public Data.Enums.DocumentationStatus Status { get; init; }
    public List<string> Warnings { get; init; } = new();
}
