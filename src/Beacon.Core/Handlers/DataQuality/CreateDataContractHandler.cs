using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
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
    BeaconConfiguration configuration,
    CustomSqlChangeApproval changeApproval) : IRequestHandler<CreateDataContractCommand, CreateDataContractResult>
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

        // Under the workflow its Custom SQL rules wait for a second person's approval: the caller is the requester.
        var requester = await changeApproval.RequesterAsync(null, hasCustomSql, cancellationToken);

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

        var recipients = request.RecipientIds is { Count: > 0 }
            ? await context.Recipients
                .Where(x => request.RecipientIds.Contains(x.Id))
                .ToListAsync(cancellationToken)
            : [];

        // Under the workflow each Custom SQL rule waits for approval.
        var approvedSqlHash = requester == null ? null : ChangeApprovalHashes.NothingApproved;

        DataContract NewContract() => new()
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
                        IsEnabled = x.IsEnabled,
                        ApprovedSqlHash = x.RuleType == DataContractRuleType.CustomSql ? approvedSqlHash : null
                    })
                .ToList(),
            Recipients = recipients
        };

        var created = requester == null
            ? await CreateAsync(context, NewContract, cancellationToken)
            : await CreateForApprovalAsync(context, requester, NewContract, recipients, cancellationToken);

        if (created.IsEnabled)
        {
            await scheduler.AddOrUpdateDataQualityJob(created.Id, created.Name, created.CronExpression);
        }

        return new CreateDataContractResult
        {
            DataContractId = created.Id,
            AwaitingApproval = requester != null,
            Message = requester == null ? null : CreateDataContractResult.SubmittedForApprovalMessage
        };
    }

    private static async Task<DataContract> CreateAsync(BeaconContext context, Func<DataContract> newContract, CancellationToken cancellationToken)
    {
        var contract = newContract();
        context.DataContracts.Add(contract);
        await context.SaveChangesAsync(cancellationToken);

        return contract;
    }

    // The contract and its request in one unit of work: the contract's save gives it the id its request names, and both
    // commit together. Through the execution strategy, so a host that enables retry-on-failure gets a retried unit; a
    // retry starts from a clean tracker and a new contract. The recipients it attaches were read before the tracker was
    // cleared: they are attached again as they are stored (unchanged), so the save writes only their links to the contract.
    private async Task<DataContract> CreateForApprovalAsync(
        BeaconContext context,
        ChangeRequester requester,
        Func<DataContract> newContract,
        List<Recipient> recipients,
        CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            context.AttachRange(recipients);
            var contract = newContract();
            context.DataContracts.Add(contract);
            await context.SaveChangesAsync(cancellationToken);

            await changeApproval.SubmitAsync(context, requester, contract, [], 0, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return contract;
        });
    }
}

/// <summary>
/// Creates a data contract owned by the signed-in caller. Its owner or an Admin may later change, disable, retarget or
/// delete it; a contract with a Custom SQL rule is an Admin's only. While the change approval workflow covers Custom SQL
/// rules, its Custom SQL rules are submitted for approval and are not evaluated until a second person approved them.
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
    /// <summary>What a create says when its Custom SQL rules were submitted for approval.</summary>
    public const string SubmittedForApprovalMessage =
        "Submitted for approval: the contract's Custom SQL rules are not evaluated until a second person approves them.";

    public int DataContractId { get; init; }

    /// <summary>The contract's Custom SQL rules were submitted for approval and are not evaluated until approved.</summary>
    public bool AwaitingApproval { get; init; }

    /// <summary>What happens next, when the contract waits for approval; null otherwise.</summary>
    public string? Message { get; init; }
}
