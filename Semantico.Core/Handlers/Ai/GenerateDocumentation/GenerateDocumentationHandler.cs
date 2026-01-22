using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.GenerateDocumentation;

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
    public Semantico.Core.Data.Enums.DocumentationStatus Status { get; init; }
    public List<string> Warnings { get; init; } = new();
}
