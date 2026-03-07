using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities.DataQuality;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.DataQuality;
using Semantico.Core.Worker;

namespace Semantico.Core.Handlers.DataQuality.CreateDataContract;

internal sealed class CreateDataContractHandler(
    IDbContextFactory<SemanticoContext> contextFactory,
    ISemanticoScheduler scheduler) : IRequestHandler<CreateDataContractCommand, CreateDataContractResult>
{
    public async Task<CreateDataContractResult> Handle(CreateDataContractCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var contract = new DataContract
        {
            DataSourceId = request.DataSourceId,
            SchemaName = request.SchemaName,
            TableName = request.TableName,
            Name = request.Name,
            Description = request.Description,
            CronExpression = request.CronExpression,
            IsEnabled = request.IsEnabled,
            OwnerUserId = request.OwnerUserId,
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
            scheduler.AddOrUpdateDataQualityJob(contract.Id, contract.Name, contract.CronExpression);
        }

        return new CreateDataContractResult { DataContractId = contract.Id };
    }
}

public record CreateDataContractCommand(
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
) : IRequest<CreateDataContractResult>;

public record CreateDataContractResult
{
    public int DataContractId { get; init; }
}
