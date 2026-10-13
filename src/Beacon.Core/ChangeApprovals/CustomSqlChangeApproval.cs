using Beacon.Core.Data;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// A data contract's Custom SQL rules, under the change approval workflow (<see cref="ApprovalWorkflowOptions.CoversCustomSqlRules"/>).
/// A save rewrites a contract's rules, so each new Custom SQL rule takes the approval state of a rule it replaces with the
/// same SQL on the same data source (approved, not governed, or waiting); one whose SQL or data source changed keeps the
/// SQL approved for the rule of its name (for a rule not governed yet, the SQL it ran), so it waits for approval and putting
/// that SQL back needs none; a new one waits with nothing approved. A rule waiting is not evaluated. The rules waiting form
/// one request per contract; the other rules keep being evaluated. With the workflow off, a new or changed Custom SQL rule
/// is not governed.
/// </summary>
internal sealed class CustomSqlChangeApproval(ChangeApprovalSubmitter submitter)
{
    private const ChangeApprovalKind Kind = ChangeApprovalKind.CustomSqlRules;

    /// <summary>
    /// The requester of a save of a contract that carries (or carried) Custom SQL rules; null when the workflow does not
    /// cover them or <paramref name="hasCustomSql"/> is false. Resolved before anything is written.
    /// </summary>
    public async Task<ChangeRequester?> RequesterAsync(int? contractId, bool hasCustomSql, CancellationToken cancellationToken)
    {
        return hasCustomSql
            ? await submitter.RequesterAsync(Kind, contractId, cancellationToken)
            : null;
    }

    /// <summary>True when the workflow covers Custom SQL rules.</summary>
    public bool Covers()
    {
        return submitter.Covers(Kind);
    }

    /// <summary>The contract's row, taken first in a unit of work that changes its rules (a no-op update; 0 when it is gone).</summary>
    public static Task<int> LockAsync(BeaconContext context, int contractId, CancellationToken cancellationToken)
    {
        return context.DataContracts
            .Where(x => x.Id == contractId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.IsEnabled, y => y.IsEnabled), cancellationToken);
    }

    /// <summary>
    /// Sets the approved hash of each of <paramref name="contract"/>'s new rules from <paramref name="replaced"/> (the rules
    /// the save replaces, on <paramref name="replacedDataSourceId"/>; none for a new contract), before they are saved.
    /// </summary>
    public static void CarryApprovals(DataContract contract, IReadOnlyList<DataContractRule> replaced, int replacedDataSourceId, bool governed)
    {
        // Each replaced rule's state carries over to one new rule: one with the same SQL on the same data source first, then,
        // under the workflow, one of the same name, which keeps the SQL approved for it.
        var carried = replaced
            .Where(x => x.RuleType == DataContractRuleType.CustomSql)
            .Select(x => new CarriedRule(x.Name, CustomSqlRuleDefinition.Hash(replacedDataSourceId, x.Configuration), x.ApprovedSqlHash))
            .ToList();
        var rules = contract.Rules
            .Where(x => x.RuleType == DataContractRuleType.CustomSql)
            .ToList();
        var unmatched = new List<DataContractRule>();

        foreach (var rule in rules)
        {
            var hash = CustomSqlRuleDefinition.Hash(contract.DataSourceId, rule.Configuration);
            var match = carried.FindIndex(x => x.Hash == hash);
            if (match < 0)
            {
                unmatched.Add(rule);
                continue;
            }

            rule.ApprovedSqlHash = carried[match].ApprovedSqlHash;
            carried.RemoveAt(match);
        }

        foreach (var rule in unmatched)
        {
            var named = governed ? carried.FindIndex(x => x.Name == rule.Name) : -1;
            if (named < 0)
            {
                rule.ApprovedSqlHash = governed ? ChangeApprovalHashes.NothingApproved : null;
                continue;
            }

            // A rule not governed yet ran its SQL before the workflow: that SQL counts as approved.
            rule.ApprovedSqlHash = carried[named].ApprovedSqlHash ?? carried[named].Hash;
            carried.RemoveAt(named);
        }
    }

    /// <summary>
    /// The contract's request after its rules were set (<see cref="CarryApprovals"/>) and, for a new contract, saved once
    /// (it needs its id): the rules that wait for approval, against those that waited before. None when no rule waits now
    /// or waited before. Without a requester (the workflow does not cover Custom SQL rules) nothing is submitted, and a
    /// save that changed which rules wait supersedes the pending request that no longer matches them.
    /// </summary>
    public async Task<ChangeSubmission?> SubmitAsync(
        BeaconContext context,
        ChangeRequester? requester,
        DataContract contract,
        IReadOnlyList<DataContractRule> replaced,
        int replacedDataSourceId,
        CancellationToken cancellationToken)
    {
        var previousWaiting = WaitingHashes(replaced, replacedDataSourceId);
        var waiting = WaitingHashes(contract.Rules, contract.DataSourceId);
        if (previousWaiting.Count == 0 && waiting.Count == 0)
        {
            return null;
        }

        var previousSetHash = CustomSqlRuleDefinition.SetHash(previousWaiting);
        var setHash = CustomSqlRuleDefinition.SetHash(waiting);
        if (requester == null)
        {
            // Only a request of the rules that waited can be pending; one that no longer matches them is moot.
            return previousWaiting.Count == 0 || previousSetHash == setHash
                ? null
                : await submitter.SupersedeOrphanedAsync(context, Kind, contract.Id, waiting.Count == 0 ? null : setHash, cancellationToken);
        }

        var previous = View(replaced, replacedDataSourceId);
        var proposed = View(contract.Rules, contract.DataSourceId);

        var proposal = new ChangeProposal(
            Kind,
            contract.Id,
            contract.Name,
            previousSetHash,
            setHash,
            NeedsApproval: waiting.Count > 0,
            proposed,
            replaced.Count == 0 ? null : previous,
            SummaryOf(waiting.Count, replaced.Count == 0));

        return await submitter.SubmitAsync(context, requester, proposal, cancellationToken);
    }

    /// <summary>Tells the requesters of the requests a submission superseded, after its unit of work committed.</summary>
    public Task NotifySupersededAsync(ChangeSubmission? submission, CancellationToken cancellationToken)
    {
        return submitter.NotifySupersededAsync(submission, cancellationToken);
    }

    private static List<string> WaitingHashes(IEnumerable<DataContractRule> rules, int dataSourceId)
    {
        return rules
            .Where(x => CustomSqlRuleDefinition.Awaits(x, dataSourceId))
            .Select(x => CustomSqlRuleDefinition.Hash(dataSourceId, x.Configuration))
            .ToList();
    }

    private static CustomSqlRulesView View(IEnumerable<DataContractRule> rules, int dataSourceId)
    {
        return new CustomSqlRulesView
        {
            DataSourceId = dataSourceId,
            Rules = rules
                .Where(x => x.RuleType == DataContractRuleType.CustomSql)
                .Select(x =>
                    new CustomSqlRuleView
                    {
                        Name = x.Name,
                        Sql = DataQualityRuleGuard.TryReadSql(x.Configuration) ?? string.Empty,
                        AwaitsApproval = CustomSqlRuleDefinition.Awaits(x, dataSourceId)
                    })
                .ToList()
        };
    }

    private static string SummaryOf(int waiting, bool newContract)
    {
        if (waiting == 0)
        {
            return "Custom SQL rules waiting for approval removed or put back to their approved SQL";
        }

        var rules = waiting == 1 ? "1 Custom SQL rule" : $"{waiting} Custom SQL rules";

        return newContract ? $"Contract created with {rules}" : $"{rules} added or changed";
    }

    private sealed record CarriedRule(string Name, string Hash, string? ApprovedSqlHash);
}
