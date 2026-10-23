using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Core.Worker;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.DataQuality.UpdateDataContract;

internal sealed class UpdateDataContractHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconScheduler scheduler,
    IBeaconUserContext userContext,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases,
    IBeaconActorAccessor actorAccessor,
    CustomSqlChangeApproval changeApproval,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider,
    ILogger<UpdateDataContractHandler> logger) : IRequestHandler<UpdateDataContractCommand>
{
    /// <summary>Why a save is refused when the contract's Custom SQL rules changed while it was being saved.</summary>
    internal const string RulesChangedMessage =
        "The contract's rules changed while it was being saved, and a change of its Custom SQL rules waits for approval: reload it and save again.";

    /// <summary>Why a save is refused when another save replaced the contract's rules while it was being saved.</summary>
    internal const string RulesReplacedMessage =
        "The contract's rules were changed by another save while this one was being saved: reload it and save again.";

    public async Task Handle(UpdateDataContractCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Changing, disabling or retargeting a contract is its owner's or an Admin's; checked before anything else is
        // read or written.
        var owner = await context.DataContracts
            .Where(x => x.Id == request.DataContractId)
            .Select(x =>
                new
                {
                    x.OwnerUserId
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw DataContractOwnership.Missing(actor, request.DataContractId, logger);
        DataContractOwnership.EnsureOwnerOrAdmin(actor, request.DataContractId, owner.OwnerUserId, logger);

        // Checked before the transaction: a contract that carries a CustomSql rule, now or after this update, is
        // changed by an Admin only, and the new rules' SQL must pass the read-only gate.
        var addsCustomSql = DataQualityRuleGuard.HasCustomSql(request.Rules);
        var hasCustomSql = addsCustomSql || await context.DataContractRules
            .Where(x => x.DataContractId == request.DataContractId)
            .Where(x => x.RuleType == DataContractRuleType.CustomSql)
            .AnyAsync(cancellationToken);
        if (hasCustomSql)
        {
            DataQualityRuleGuard.EnsureAdmin(userContext);
        }

        // Under the workflow a new or changed Custom SQL rule waits for a second person's approval: the caller is the
        // requester.
        var requester = await changeApproval.RequesterAsync(request.DataContractId, hasCustomSql, cancellationToken);

        if (addsCustomSql)
        {
            // The whole row: the rule's SQL may name only the database its connection is configured for.
            var dataSource = await context.DataSources
                .Where(x => x.Id == request.DataSourceId)
                .FirstOrDefaultAsync(cancellationToken);

            DataQualityRuleGuard.EnsureCustomSqlRunnable(request.Rules, DataQualityRuleTarget.Of(dataSource, configuredDatabases), gate);
        }

        // The rules name only the (new) data source's own tables and columns, stored as its metadata spells them.
        var catalog = await DataQualityRuleCatalog.LoadAsync(
            context,
            request.DataSourceId,
            request.SchemaName,
            request.TableName,
            request.Rules.Select(x => x.Configuration),
            cancellationToken);
        var confined = DataQualityRuleGuard.EnsureConfined(catalog, request.SchemaName, request.TableName, request.Rules);

        // Retiring the replaced rules runs as a bulk update outside the change tracker, so it would commit on its own:
        // the retirement, the new rules and the save share one explicit transaction.
        await using var tx = await context.Database.BeginTransactionAsync(cancellationToken);

        // Under the workflow the contract's row first, as a decision on its Custom SQL rules takes it.
        if (requester != null)
        {
            await CustomSqlChangeApproval.LockAsync(context, request.DataContractId, cancellationToken);
        }

        var contract = await context.DataContracts
            .Include(c => c.Recipients)
            .Where(c => c.Id == request.DataContractId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Data contract {request.DataContractId} not found");

        // The rules this save replaces, on the data source they ran on: an unchanged Custom SQL rule keeps its approval.
        // Read untracked: they are retired below, never removed, so the results of past evaluations keep pointing at the
        // definition that produced them.
        var replaced = await context.DataContractRules
            .AsNoTracking()
            .Where(x => x.DataContractId == request.DataContractId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var replacedDataSourceId = contract.DataSourceId;

        // Re-checked on the rows this transaction rewrites: a CustomSql rule added after the check above is an Admin's, and
        // under the workflow its change needs the requester the check above did not resolve: refused, nothing written.
        if (replaced.Any(x => x.RuleType == DataContractRuleType.CustomSql))
        {
            DataQualityRuleGuard.EnsureAdmin(userContext);

            if (requester == null && changeApproval.Covers())
            {
                throw new BeaconConflictException(RulesChangedMessage);
            }
        }

        contract.DataSourceId = request.DataSourceId;
        contract.Name = request.Name;
        contract.Description = request.Description;
        contract.SchemaName = confined.SchemaName;
        contract.TableName = confined.TableName;
        contract.CronExpression = request.CronExpression;
        contract.IsEnabled = request.IsEnabled;
        // OwnerUserId is intentionally NOT updated through the generic update command;
        // ownership transfer must go through a dedicated admin endpoint to prevent
        // body-supplied user-id impersonation.
        contract.AlertOnFailure = request.AlertOnFailure;
        contract.FailureThresholdScore = request.FailureThresholdScore;

        // Replace rules: the existing ones are retired (they and their results stay), the new ones added. Each rule read
        // above is retired once: one another save retired meanwhile means its rules replaced these, refused.
        if (replaced.Count > 0)
        {
            var replacedIds = replaced
                .Select(x => x.Id)
                .ToList();
            var retiredTime = timeProvider.GetUtcNow().UtcDateTime;
            var retired = await context.DataContractRules
                .Where(x => x.DataContractId == request.DataContractId)
                .Where(x => replacedIds.Contains(x.Id))
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ArchivedTime, retiredTime), cancellationToken);
            if (retired != replaced.Count)
            {
                throw new BeaconConflictException(RulesReplacedMessage);
            }
        }

        contract.Rules = request.Rules
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
            .ToList();
        CustomSqlChangeApproval.CarryApprovals(contract, replaced, replacedDataSourceId, governed: requester != null);
        var submission = await changeApproval.SubmitAsync(context, requester, contract, replaced, replacedDataSourceId, cancellationToken);

        // Replace recipients
        contract.Recipients.Clear();
        if (request.RecipientIds is { Count: > 0 })
        {
            var recipients = await context.Recipients
                .Where(r => request.RecipientIds.Contains(r.Id))
                .ToListAsync(cancellationToken);
            contract.Recipients = recipients;
        }

        contract.RecordChangeBy(actor, timeProvider);
        await securityAudit.SaveWithEventsAsync(
            context,
            () => DataContractAuditEntries.With(DataContractAuditEntries.Updated(contract, replacedDataSourceId, submission), submission),
            cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await changeApproval.NotifySupersededAsync(submission, cancellationToken);

        if (contract.IsEnabled)
        {
            await scheduler.AddOrUpdateDataQualityJob(contract.Id, contract.Name, contract.CronExpression);
        }
        else
        {
            await scheduler.RemoveDataQualityJob(contract.Id, contract.Name);
        }
    }
}

public record UpdateDataContractCommand(
    int DataContractId,
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
) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DataContract(DataContractId), ProjectAction.Write),
        new(ProjectResource.DataSource(DataSourceId), ProjectAction.Write)
    ];
}
