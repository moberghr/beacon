using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Services.Ai;

namespace Semantico.Core.Handlers.Ai.GenerateAlert;

internal sealed class GenerateAlertHandler
    : IRequestHandler<GenerateAlertCommand, GenerateAlertResult>
{
    private readonly IAiAlertGenerationService _alertService;
    private readonly ILogger<GenerateAlertHandler> _logger;

    public GenerateAlertHandler(
        IAiAlertGenerationService alertService,
        ILogger<GenerateAlertHandler> logger)
    {
        _alertService = alertService;
        _logger = logger;
    }

    public async Task<GenerateAlertResult> Handle(
        GenerateAlertCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Generating alert for DataSource {DataSourceId}",
            request.DataSourceId);

        var startTime = DateTime.UtcNow;

        var alertConfig = await _alertService.GenerateAlertAsync(
            request.DataSourceId,
            request.NaturalLanguageDescription,
            request.CreatedBy,
            request.Options,
            cancellationToken);

        var generationTime = DateTime.UtcNow - startTime;

        _logger.LogInformation(
            "Alert configuration generated successfully: {AlertConfigId}",
            alertConfig.Id);

        return new GenerateAlertResult
        {
            AlertConfigurationId = alertConfig.Id,
            Name = alertConfig.Name,
            GeneratedSql = alertConfig.GeneratedSql,
            Status = alertConfig.Status,
            ValidationErrors = alertConfig.ValidationErrors,
            TokensUsed = alertConfig.TokensUsed,
            EstimatedCost = alertConfig.EstimatedCost,
            GenerationTime = generationTime,
            GeneratedByModel = alertConfig.GeneratedByModel,
            ConfidenceScore = alertConfig.ConfidenceScore
        };
    }
}

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
