using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Core.Worker;

namespace Beacon.Core.Handlers.DataQuality.CreateDataContract;

internal sealed class CreateDataContractHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconScheduler scheduler,
    IBeaconUserContext userContext,
    ISqlExecutionGate gate,
    IBeaconActorAccessor actorAccessor) : IRequestHandler<CreateDataContractCommand, CreateDataContractResult>
{
    public async Task<CreateDataContractResult> Handle(CreateDataContractCommand request, CancellationToken cancellationToken)
    {
        var hasCustomSql = DataQualityRuleGuard.HasCustomSql(request.Rules);
        if (hasCustomSql)
        {
            DataQualityRuleGuard.EnsureAdmin(userContext);
        }

        // The owner is the signed-in caller, never a value from the request; a contract is never stored without one.
        var owner = await actorAccessor.GetCurrentAsync(cancellationToken);
        var ownerUserId = owner.UserId
            ?? throw new InvalidOperationException("The caller has no resolvable user to own the data contract.");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (hasCustomSql)
        {
            var dataSource = await context.DataSources
                .Where(x => x.Id == request.DataSourceId)
                .Select(x =>
                    new DataQualityRuleTarget(x.DataSourceType, x.DatabaseEngineType, x.HostManagedKey))
                .FirstOrDefaultAsync(cancellationToken);

            DataQualityRuleGuard.EnsureCustomSqlRunnable(request.Rules, dataSource, gate);
        }

        var contract = new DataContract
        {
            DataSourceId = request.DataSourceId,
            SchemaName = request.SchemaName,
            TableName = request.TableName,
            Name = request.Name,
            Description = request.Description,
            CronExpression = request.CronExpression,
            IsEnabled = request.IsEnabled,
            OwnerUserId = ownerUserId,
            AlertOnFailure = request.AlertOnFailure,
            FailureThresholdScore = request.FailureThresholdScore,
            Rules = request.Rules.Select(r => new DataContractRule
            {
                Name = r.Name,
                Description = r.Description,
                RuleType = r.RuleType,
                ColumnName = r.ColumnName,
                Configuration = r.Configuration,
                Severity = r.Severity,
                Weight = r.Weight,
                IsEnabled = r.IsEnabled
            }).ToList()
        };

        if (request.RecipientIds is { Count: > 0 })
        {
            var recipients = await context.Recipients
                .Where(r => request.RecipientIds.Contains(r.Id))
                .ToListAsync(cancellationToken);
            contract.Recipients = recipients;
        }

        context.DataContracts.Add(contract);
        await context.SaveChangesAsync(cancellationToken);

        if (contract.IsEnabled)
        {
            await scheduler.AddOrUpdateDataQualityJob(contract.Id, contract.Name, contract.CronExpression);
        }

        return new CreateDataContractResult { DataContractId = contract.Id };
    }
}

/// <summary>
/// Creates a data contract owned by the signed-in caller. Its owner or an Admin may later change, disable, retarget or
/// delete it; a contract with a Custom SQL rule is an Admin's only.
/// </summary>
public record CreateDataContractCommand(
    int DataSourceId,
    string SchemaName,
    string TableName,
    string Name,
    string? Description,
    string CronExpression,
    bool IsEnabled,
    bool AlertOnFailure,
    int FailureThresholdScore,
    List<DataContractRuleData> Rules,
    List<int>? RecipientIds = null
) : IRequest<CreateDataContractResult>;

public record CreateDataContractResult
{
    public int DataContractId { get; init; }
}
