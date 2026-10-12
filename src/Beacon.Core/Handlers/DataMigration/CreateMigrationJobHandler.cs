using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataMigration;

internal sealed class CreateMigrationJobHandler(
    IMigrationService migrationService,
    MigrationJobAccess access,
    MigrationWritePolicy writePolicy)
    : IRequestHandler<CreateMigrationJobCommand, CreateMigrationJobResult>
{
    public async Task<CreateMigrationJobResult> Handle(
        CreateMigrationJobCommand request,
        CancellationToken cancellationToken)
    {
        writePolicy.DemandEnabled();

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

        await access.DemandAsync(request.DataSourceId, request.QueryText, request.DestinationDataSourceId, cancellationToken);
        await writePolicy.DemandTargetsAsync(request.DataSourceId, request.QueryText, request.DestinationDataSourceId, cancellationToken);

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

/// <summary>
/// Creates a migration job. For an Admin, or a MigrationOperator who may query the source and the data source of every
/// step in <c>QueryText</c> and write the destination, and whose destination is no more widely reachable than any of those
/// sources (<see cref="MigrationJobAccess"/>). Only while the deployment runs migrations, into a migration write target,
/// from sources that are not one (<see cref="MigrationWritePolicy"/>).
/// </summary>
[ProjectScopeExempt("gated by the MigrationOperator permission; the handler checks Query on the source and every step's data source and Write on the destination through project access", Policy = BeaconPermissions.MigrationOperatorPolicy)]
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
