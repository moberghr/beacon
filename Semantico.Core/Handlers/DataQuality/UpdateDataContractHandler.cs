using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities.DataQuality;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;
using Semantico.Core.Models.DataQuality;
using Semantico.Core.Worker;

namespace Semantico.Core.Handlers.DataQuality.UpdateDataContract;

internal sealed class UpdateDataContractHandler(
    IDbContextFactory<SemanticoContext> contextFactory,
    ISemanticoScheduler scheduler) : IRequestHandler<UpdateDataContractCommand>
{
    public async Task Handle(UpdateDataContractCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await context.DataContracts
            .Include(c => c.Rules)
            .Include(c => c.Recipients)
            .FirstOrDefaultAsync(c => c.Id == request.DataContractId, cancellationToken)
            ?? throw new SemanticoException($"Data contract {request.DataContractId} not found");

        contract.DataSourceId = request.DataSourceId;
        contract.Name = request.Name;
        contract.Description = request.Description;
        contract.SchemaName = request.SchemaName;
        contract.TableName = request.TableName;
        contract.CronExpression = request.CronExpression;
        contract.IsEnabled = request.IsEnabled;
        contract.OwnerUserId = request.OwnerUserId;
        contract.AlertOnFailure = request.AlertOnFailure;
        contract.FailureThresholdScore = request.FailureThresholdScore;

        // Delete rule results that reference the old rules before removing them
        var oldRuleIds = contract.Rules.Select(r => r.Id).ToList();
        if (oldRuleIds.Count > 0)
        {
            await context.DataQualityRuleResults
                .Where(rr => oldRuleIds.Contains(rr.DataContractRuleId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        // Replace rules: remove existing, add new
        context.DataContractRules.RemoveRange(contract.Rules);

        contract.Rules = request.Rules.Select(r => new DataContractRule
        {
            Name = r.Name,
            Description = r.Description,
            RuleType = r.RuleType,
            ColumnName = r.ColumnName,
            Configuration = r.Configuration,
            Severity = r.Severity,
            Weight = r.Weight,
            IsEnabled = r.IsEnabled
        }).ToList();

        // Replace recipients
        contract.Recipients.Clear();
        if (request.RecipientIds is { Count: > 0 })
        {
            var recipients = await context.Recipients
                .Where(r => request.RecipientIds.Contains(r.Id))
                .ToListAsync(cancellationToken);
            contract.Recipients = recipients;
        }

        await context.SaveChangesAsync(cancellationToken);

        if (contract.IsEnabled)
        {
            scheduler.AddOrUpdateDataQualityJob(contract.Id, contract.Name, contract.CronExpression);
        }
        else
        {
            scheduler.RemoveDataQualityJob(contract.Id, contract.Name);
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
    string? OwnerUserId,
    bool AlertOnFailure,
    int FailureThresholdScore,
    List<DataContractRuleData> Rules,
    List<int>? RecipientIds = null
) : IRequest;
