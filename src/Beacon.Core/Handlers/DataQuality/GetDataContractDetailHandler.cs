using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Authorization;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.DataQuality.GetDataContractDetail;

internal sealed class GetDataContractDetailHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    BeaconConfiguration configuration) : IRequestHandler<GetDataContractDetailQuery, DataContractData>
{
    public async Task<DataContractData> Handle(GetDataContractDetailQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await context.DataContracts
            .Where(c => c.Id == request.DataContractId)
            .Select(c => new DataContractData
            {
                Id = c.Id,
                DataSourceId = c.DataSourceId,
                DataSourceName = c.DataSource.Name,
                SchemaName = c.SchemaName,
                TableName = c.TableName,
                Name = c.Name,
                Description = c.Description,
                CronExpression = c.CronExpression,
                IsEnabled = c.IsEnabled,
                HasOwner = c.OwnerUserId != null,
                OwnerDisplayName = context.Users
                    .Where(x => x.ExternalId == c.OwnerUserId)
                    .Select(UserLabel.OfUser)
                    .FirstOrDefault(),
                OwnerNotAuthorized = c.OwnerNotAuthorizedSince != null,
                AlertOnFailure = c.AlertOnFailure,
                FailureThresholdScore = c.FailureThresholdScore,
                CreatedTime = c.CreatedTime,
                LatestScore = context.DataQualityScores
                    .Where(s => s.DataSourceId == c.DataSourceId &&
                                s.SchemaName == c.SchemaName &&
                                s.TableName == c.TableName)
                    .Select(s => (double?)s.Score)
                    .FirstOrDefault(),
                Rules = c.Rules.Select(r => new DataContractRuleData
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description,
                    RuleType = r.RuleType,
                    ColumnName = r.ColumnName,
                    Configuration = r.Configuration,
                    Severity = r.Severity,
                    Weight = r.Weight,
                    IsEnabled = r.IsEnabled
                }).ToList(),
                // Destinations are secrets: never part of a contract read (admins see them masked on recipients).
                Recipients = c.Recipients
                    .Select(r =>
                        new DataContractRecipientData
                        {
                            Id = r.Id,
                            Name = r.Name,
                            NotificationType = r.NotificationType
                        })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Data contract {request.DataContractId} not found");

        // Only while the workflow covers Custom SQL rules does anything wait for approval.
        if (!configuration.ApprovalWorkflow.Covers(ChangeApprovalKind.CustomSqlRules))
        {
            return contract;
        }

        var governed = await context.DataContractRules
            .Where(x => x.DataContractId == request.DataContractId)
            .Where(x => x.RuleType == DataContractRuleType.CustomSql)
            .Where(x => x.ApprovedSqlHash != null)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Configuration,
                    x.ApprovedSqlHash
                })
            .ToListAsync(cancellationToken);
        var awaiting = governed
            .Where(x => !ChangeApprovalHashes.Allows(x.ApprovedSqlHash, CustomSqlRuleDefinition.Hash(contract.DataSourceId, x.Configuration)))
            .Select(x => x.Id)
            .ToHashSet();
        var pendingRequestId = await context.ChangeApprovalRequests
            .Where(x => x.Kind == ChangeApprovalKind.CustomSqlRules)
            .Where(x => x.ResourceId == request.DataContractId)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .OrderByDescending(x => x.Id)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // Rules that wait while no request of them is pending: the last decision says why (a rejection, its comment).
        var decided = awaiting.Count > 0 && pendingRequestId == null
            ? await ChangeApprovalOutcomes.LastDecidedAsync(context, ChangeApprovalKind.CustomSqlRules, [request.DataContractId], cancellationToken)
            : [];

        return contract with
        {
            Rules = contract.Rules
                .Select(x => x with { AwaitingApproval = awaiting.Contains(x.Id) })
                .ToList(),
            PendingApprovalRequestId = pendingRequestId,
            LastApprovalDecision = decided.GetValueOrDefault(request.DataContractId)
        };
    }
}

public record GetDataContractDetailQuery(int DataContractId) : IRequest<DataContractData>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DataContract(DataContractId), ProjectAction.Read)
    ];
}
