using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.GenerateAlert;

public record GenerateAlertCommand : IRequest<GenerateAlertResult>
{
    public int DataSourceId { get; init; }
    public string NaturalLanguageDescription { get; init; } = null!;
    public string CreatedBy { get; init; } = null!;
    public AlertGenerationOptions Options { get; init; } = new();
}

public record GenerateAlertResult
{
    public int AlertConfigurationId { get; init; }
    public string Name { get; init; } = null!;
    public string GeneratedSql { get; init; } = null!;
    public AlertStatus Status { get; init; }
    public string? ValidationErrors { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public TimeSpan GenerationTime { get; init; }
    public string GeneratedByModel { get; init; } = null!;
    public decimal? ConfidenceScore { get; init; }
}
