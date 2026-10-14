using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Approvals;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// Which migration jobs wait for approval while the workflow covers migration jobs: those it governs whose definition is
/// not the approved one (they do not run), with the pending request of their change, if one waits, or else the last
/// decision on it (a rejection says to delete and recreate the job). Nothing waits while the workflow does not cover
/// migration jobs.
/// </summary>
internal sealed class MigrationJobApprovalState(BeaconConfiguration configuration)
{
    /// <summary>The jobs among <paramref name="jobIds"/> that wait for approval, by id, with their pending request or last decision.</summary>
    public async Task<IReadOnlyDictionary<int, AwaitingApproval>> AwaitingAsync(BeaconContext context, IReadOnlyCollection<int> jobIds, CancellationToken cancellationToken)
    {
        if (jobIds.Count == 0 || !configuration.ApprovalWorkflow.Covers(ChangeApprovalKind.MigrationJob))
        {
            return new Dictionary<int, AwaitingApproval>();
        }

        var governed = await context.MigrationJobs
            .Where(x => jobIds.Contains(x.Id))
            .Where(x => x.ApprovedDefinitionHash != null)
            .Select(x =>
                new
                {
                    x.Id,
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
        var awaiting = governed
            .Where(x => !ChangeApprovalHashes.Allows(x.ApprovedDefinitionHash, x.Definition.Hash))
            .Select(x => x.Id)
            .ToList();
        if (awaiting.Count == 0)
        {
            return new Dictionary<int, AwaitingApproval>();
        }

        var pending = await context.ChangeApprovalRequests
            .Where(x => x.Kind == ChangeApprovalKind.MigrationJob)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Where(x => awaiting.Contains(x.ResourceId))
            .Select(x =>
                new
                {
                    x.ResourceId,
                    x.Id
                })
            .ToListAsync(cancellationToken);
        var pendingByJob = pending
            .GroupBy(x => x.ResourceId)
            .ToDictionary(x => x.Key, x => x.Max(y => y.Id));
        var decided = await ChangeApprovalOutcomes.LastDecidedAsync(
            context,
            ChangeApprovalKind.MigrationJob,
            awaiting
                .Where(x => !pendingByJob.ContainsKey(x))
                .ToList(),
            cancellationToken);

        return awaiting.ToDictionary(
            x => x,
            x => new AwaitingApproval(pendingByJob.TryGetValue(x, out var requestId) ? requestId : null, decided.GetValueOrDefault(x)));
    }
}

/// <summary>A resource waiting for approval: its pending change request, if one waits, or else the last decision on its change.</summary>
internal sealed record AwaitingApproval(int? PendingRequestId, ChangeApprovalOutcome? LastDecision);
