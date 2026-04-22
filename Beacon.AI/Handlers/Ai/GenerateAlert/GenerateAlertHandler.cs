using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.AI.Services.Ai;
using Beacon.Core.Handlers.Ai.GenerateAlert;

namespace Beacon.AI.Handlers.Ai.GenerateAlert;

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

