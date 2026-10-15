using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// What a change approval switch that is off releases, counted once at startup for the log (counts only): the resources
/// of a kind the workflow governed that hold a definition nobody approved — created and never approved, changed, or whose
/// change was rejected — and the pending change requests of that kind. While the workflow does not cover the kind, they
/// run, are evaluated or receive deliveries as they are.
/// </summary>
internal static class ReleasedChangeReport
{
    public static async Task<ReleasedChanges> CountAsync(
        IDbContextFactory<BeaconContext> contextFactory,
        RecipientSecretProtector protector,
        RecipientDestinationHasher hasher,
        ChangeApprovalKind kind,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var unapproved = kind switch
        {
            ChangeApprovalKind.MigrationJob => await UnapprovedJobsAsync(context, cancellationToken),
            ChangeApprovalKind.CustomSqlRules => await UnapprovedRulesAsync(context, cancellationToken),
            ChangeApprovalKind.RecipientDestination => await UnapprovedRecipientsAsync(context, protector, hasher, cancellationToken),
            _ => 0
        };
        var pending = await context.ChangeApprovalRequests
            .Where(x => x.Kind == kind)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .CountAsync(cancellationToken);

        return new ReleasedChanges(unapproved, pending);
    }

    private static async Task<int> UnapprovedJobsAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        var jobs = await context.MigrationJobs
            .Where(x => x.ApprovedDefinitionHash != null)
            .Select(x =>
                new
                {
                    x.ApprovedDefinitionHash,
                    Definition = new MigrationJobDefinition(
                        x.DataSourceId,
                        x.QueryText,
                        x.DestinationDataSourceId,
                        x.DestinationTable,
                        x.Mode,
                        x.TransformationScript)
                })
            .ToListAsync(cancellationToken);

        return jobs.Count(x => !ChangeApprovalHashes.Allows(x.ApprovedDefinitionHash, x.Definition.Hash));
    }

    private static async Task<int> UnapprovedRulesAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        var rules = await context.DataContractRules
            .Where(x => x.RuleType == DataContractRuleType.CustomSql)
            .Where(x => x.IsEnabled)
            .Where(x => x.ApprovedSqlHash != null)
            .Select(x =>
                new
                {
                    x.ApprovedSqlHash,
                    x.Configuration,
                    x.DataContract.DataSourceId
                })
            .ToListAsync(cancellationToken);

        return rules.Count(x => !ChangeApprovalHashes.Allows(x.ApprovedSqlHash, CustomSqlRuleDefinition.Hash(x.DataSourceId, x.Configuration)));
    }

    private static async Task<int> UnapprovedRecipientsAsync(
        BeaconContext context,
        RecipientSecretProtector protector,
        RecipientDestinationHasher hasher,
        CancellationToken cancellationToken)
    {
        var recipients = await context.Recipients
            .Where(x => x.ApprovedDestinationHash != null)
            .Select(x =>
                new
                {
                    x.ApprovedDestinationHash,
                    x.NotificationType,
                    x.Destination,
                    x.HeadersJson
                })
            .ToListAsync(cancellationToken);

        return recipients.Count(x => !RecipientDestinationDefinition.Allows(
            hasher,
            x.ApprovedDestinationHash,
            RecipientDestinationDefinition.CanonicalOfStored(protector, x.NotificationType, x.Destination, x.HeadersJson)));
    }
}

/// <summary>A kind's resources that hold a definition nobody approved, and its pending change requests.</summary>
internal sealed record ReleasedChanges(int UnapprovedResources, int PendingRequests);
