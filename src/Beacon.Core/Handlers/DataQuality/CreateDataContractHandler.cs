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
    IConfiguredDatabaseResolver configuredDatabases,
    IBeaconActorAccessor actorAccessor,
    BeaconConfiguration configuration) : IRequestHandler<CreateDataContractCommand, CreateDataContractResult>
{
    public async Task<CreateDataContractResult> Handle(CreateDataContractCommand request, CancellationToken cancellationToken)
    {
        var hasCustomSql = DataQualityRuleGuard.HasCustomSql(request.Rules);
        if (hasCustomSql)
        {
            DataQualityRuleGuard.EnsureAdmin(userContext);
        }

        // The owner is the signed-in caller, never a value from the request; a contract is never stored without one, and
        // with user management on its owner is a stored Beacon user (its evaluations act with the owner's access).
        var ownerUserId = ScheduledWorkOwner.Require(await actorAccessor.GetCurrentAsync(cancellationToken), configuration);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (hasCustomSql)
        {
            // The whole row: the rule's SQL may name only the database its connection is configured for.
            var dataSource = await context.DataSources
                .Where(x => x.Id == request.DataSourceId)
                .FirstOrDefaultAsync(cancellationToken);

            DataQualityRuleGuard.EnsureCustomSqlRunnable(request.Rules, DataQualityRuleTarget.Of(dataSource, configuredDatabases), gate);
        }

        // The rules name only the data source's own tables and columns, stored as its metadata spells them.
        var catalog = await DataQualityRuleCatalog.LoadAsync(
            context,
            request.DataSourceId,
            request.SchemaName,
            request.TableName,
            request.Rules.Select(x => x.Configuration),
            cancellationToken);
        var confined = DataQualityRuleGuard.EnsureConfined(catalog, request.SchemaName, request.TableName, request.Rules);

        var contract = new DataContract
        {
            DataSourceId = request.DataSourceId,
            SchemaName = confined.SchemaName,
            TableName = confined.TableName,
            Name = request.Name,
            Description = request.Description,
            CronExpression = request.CronExpression,
            IsEnabled = request.IsEnabled,
            OwnerUserId = ownerUserId,
            AlertOnFailure = request.AlertOnFailure,
            FailureThresholdScore = request.FailureThresholdScore,
            Rules = request.Rules
                .Select((x, i) =>
                    new DataContractRule
                    {
                        Name = x.Name,
                        Description = x.Description,
                        RuleType = x.RuleType,
                        ColumnName = confined.Rules[i].ColumnName,
                        Configuration = confined.Rules[i].Configuration ?? x.Configuration,
                        Severity = x.Severity,
                        Weight = x.Weight,
                        IsEnabled = x.IsEnabled
                    })
                .ToList()
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
    [property: ProjectScopeExempt("recipients are project-less; attaching them needs Write on the contract's data source, checked")] List<int>? RecipientIds = null
) : IRequest<CreateDataContractResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DataSource(DataSourceId), ProjectAction.Write)
    ];
}

public record CreateDataContractResult
{
    public int DataContractId { get; init; }
}
