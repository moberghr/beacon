using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.DataSources;
using Semantico.Core.Models.Queries;

namespace Semantico.Core.Services;

public interface IDataSourceService
{
    Task<BaseResponse> CreateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken);

    Task UpdateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken);

    Task DeleteDataSource(int dataSourceId, CancellationToken cancellationToken);

    Task<List<DataSourceListData>> GetDataSources(int? dataSourceId, CancellationToken cancellationToken);
}

internal class DataSourceService(IDbContextFactory<SemanticoContext> contextFactory) : IDataSourceService
{
    public async Task<BaseResponse> CreateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = new DataSource
        {
            Name = dataSourceData.Name,
            ConnectionString = dataSourceData.ConnectionString,
            DatabaseEngineType = dataSourceData.DatabaseEngineType
        };

        context.DataSources.Add(dataSource);
        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Success = true
        };
    }

    public async Task DeleteDataSource(int dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .SingleAsync(cancellationToken);

        if (dataSource.QuerySteps.Count > 0)
        {
            throw new SemanticoException($"Unable to remove data source due to existing query steps");
        }

        dataSource.Archive();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<DataSourceListData>> GetDataSources(int? dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.DataSources
            .Include(x => x.QuerySteps)
                .ThenInclude(qs => qs.Query)
                    .ThenInclude(q => q.Subscriptions)
            .Include(x => x.QuerySteps)
                .ThenInclude(qs => qs.Parameters)
            .WhereIf(dataSourceId.HasValue, x => x.Id == dataSourceId)
            .Select(x => new DataSourceListData
            {
                Id = x.Id,
                Name = x.Name,
                DatabaseEngineType = x.DatabaseEngineType,
                Queries = x.QuerySteps
                    .GroupBy(qs => qs.QueryId)
                    .Select(g => new QueryData
                    {
                        QueryId = g.Key,
                        Name = g.First().Query.Name,
                        Description = g.First().Query.Description,
                        CreatedTime = g.First().Query.CreatedTime,
                        SubscriptionsCount = g.First().Query.Subscriptions.Count,
                        Steps = g.OrderBy(qs => qs.StepOrder).Select(qs => new QueryStepData
                        {
                            StepId = qs.Id,
                            StepOrder = qs.StepOrder,
                            Name = qs.Name ?? $"Step {qs.StepOrder}",
                            Description = qs.Description,
                            SqlValue = qs.SqlValue,
                            DataSourceId = qs.DataSourceId,
                            DataSourceName = x.Name,
                            DatabaseEngineType = x.DatabaseEngineType,
                            Parameters = qs.Parameters.Select(p => new QueryStepParameterData
                            {
                                Name = p.Name,
                                Type = p.Type,
                                Description = p.Description,
                                Placeholder = p.Placeholder
                            }).ToList()
                        }).ToList()
                    }).ToList()
            })
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceData.DataSourceId)
            .SingleAsync(cancellationToken);

        dataSource.Name = dataSourceData.Name;
        dataSource.ConnectionString = dataSourceData.ConnectionString;
        dataSource.DatabaseEngineType = dataSourceData.DatabaseEngineType;

        await context.SaveChangesAsync(cancellationToken);
    }
}
