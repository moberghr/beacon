using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class CreateMigrationJobHandler(IMigrationService migrationService)
    : IRequestHandler<CreateMigrationJobCommand, CreateMigrationJobResult>
{
    public async Task<CreateMigrationJobResult> Handle(
        CreateMigrationJobCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Migration job name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new InvalidOperationException("Migration job description is required.");
        }

        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("A source data source is required.");
        }

        if (request.DestinationDataSourceId <= 0)
        {
            throw new InvalidOperationException("A destination data source is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationTable))
        {
            throw new InvalidOperationException("Destination table is required.");
        }

        if (string.IsNullOrWhiteSpace(request.QueryText))
        {
            throw new InvalidOperationException("Source query text is required.");
        }

        var serviceRequest = new CreateMigrationJobRequest
        {
            Name = request.Name,
            Description = request.Description,
            DataSourceId = request.DataSourceId,
            QueryText = request.QueryText,
            DestinationDataSourceId = request.DestinationDataSourceId,
            DestinationTable = request.DestinationTable,
            Mode = request.Mode,
            IsEnabled = request.IsEnabled,
            Schedule = request.Schedule,
            MaxRetries = request.MaxRetries,
            TimeoutMinutes = request.TimeoutMinutes,
            ValidateBeforeExecution = request.ValidateBeforeExecution,
            TransformationScript = request.TransformationScript,
        };

        var response = await migrationService.CreateMigrationJob(serviceRequest, cancellationToken);

        return new CreateMigrationJobResult(
            response.MigrationJobId,
            response.Success,
            response.ErrorMessage);
    }
}

public record CreateMigrationJobCommand(
    string Name,
    string Description,
    int DataSourceId,
    string QueryText,
    int DestinationDataSourceId,
    string DestinationTable,
    MigrationMode Mode = MigrationMode.Insert,
    bool IsEnabled = true,
    string? Schedule = null,
    int MaxRetries = 3,
    int TimeoutMinutes = 30,
    bool ValidateBeforeExecution = true,
    string? TransformationScript = null) : IRequest<CreateMigrationJobResult>;

public record CreateMigrationJobResult(
    int MigrationJobId,
    bool Success,
    string? ErrorMessage);
