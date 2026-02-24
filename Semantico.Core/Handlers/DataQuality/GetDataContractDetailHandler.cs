using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Models;
using Semantico.Core.Models.DataQuality;

namespace Semantico.Core.Handlers.DataQuality.GetDataContractDetail;

internal sealed class GetDataContractDetailHandler(
    IDbContextFactory<SemanticoContext> contextFactory) : IRequestHandler<GetDataContractDetailQuery, DataContractData>
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
                OwnerUserId = c.OwnerUserId,
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
                }).ToList()
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new SemanticoException($"Data contract {request.DataContractId} not found");

        return contract;
    }
}

public record GetDataContractDetailQuery(int DataContractId) : IRequest<DataContractData>;
