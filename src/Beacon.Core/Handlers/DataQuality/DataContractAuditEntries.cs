using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.DataQuality;

/// <summary>
/// The security audit events of a data contract: ids, rule counts, the recipients it alerts and the change request its
/// Custom SQL rules wait on — never its name, description, rule SQL or the values an evaluation read.
/// </summary>
internal static class DataContractAuditEntries
{
    public static SecurityAuditEntry Created(DataContract contract, ChangeSubmission? submission)
    {
        return new SecurityAuditEntry(SecurityAuditActions.ContractCreated)
            .On(SecurityAuditResources.DataContract, contract.Id)
            .With(Definition(contract, submission).Add("dataSourceId", contract.DataSourceId));
    }

    public static SecurityAuditEntry Updated(DataContract contract, int previousDataSourceId, ChangeSubmission? submission)
    {
        return new SecurityAuditEntry(SecurityAuditActions.ContractUpdated)
            .On(SecurityAuditResources.DataContract, contract.Id)
            .With(Definition(contract, submission)
                .Add("previousDataSourceId", previousDataSourceId)
                .Add("dataSourceId", contract.DataSourceId));
    }

    public static SecurityAuditEntry Archived(int contractId)
    {
        return new SecurityAuditEntry(SecurityAuditActions.ContractArchived).On(SecurityAuditResources.DataContract, contractId);
    }

    public static SecurityAuditEntry Evaluated(DataQualityEvaluationData evaluation)
    {
        return new SecurityAuditEntry(SecurityAuditActions.ContractEvaluated)
            .On(SecurityAuditResources.DataContract, evaluation.DataContractId)
            .With(new SecurityAuditDetails()
                .Add("evaluationId", evaluation.Id)
                .Add("score", (int)Math.Round(evaluation.OverallScore, MidpointRounding.AwayFromZero))
                .Add("passedRules", evaluation.PassedRules)
                .Add("failedRules", evaluation.FailedRules)
                .Add("totalRules", evaluation.TotalRules)
                .Add("awaitingApprovalRules", evaluation.AwaitingApprovalRules));
    }

    /// <summary>The events of a save: the contract's, then the change request it submitted, when it submitted one.</summary>
    public static IEnumerable<SecurityAuditEntry> With(SecurityAuditEntry contractEvent, ChangeSubmission? submission)
    {
        return new[] { contractEvent }.Concat(ChangeApprovalAuditEntries.Submitted(submission));
    }

    private static SecurityAuditDetails Definition(DataContract contract, ChangeSubmission? submission)
    {
        return new SecurityAuditDetails()
            .Add("ruleCount", contract.Rules.Count)
            .Add("customSqlRuleCount", contract.Rules.Count(x => x.RuleType == DataContractRuleType.CustomSql))
            .Add("recipientIds", contract.Recipients.Select(x => x.Id))
            .Add("isEnabled", contract.IsEnabled)
            .Add("changeApprovalId", submission?.Request?.Id);
    }
}
