using System.Data;
using Dapper;
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

    Task<AdHocQueryResult> ExecuteAdHocQuery(int dataSourceId, string query, CancellationToken cancellationToken);
}

internal class DataSourceService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IEncryptionService encryptionService) : IDataSourceService
{
    public async Task<BaseResponse> CreateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = new DataSource
        {
            Name = dataSourceData.Name,
            ConnectionString = encryptionService.Encrypt(dataSourceData.ConnectionString),
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
            .Include(x => x.QuerySteps)
                .ThenInclude(qs => qs.Query)
            .Where(x => x.Id == dataSourceId)
            .SingleAsync(cancellationToken);

        // Check for non-archived queries using this data source
        var unarchivedQueries = dataSource.QuerySteps
            .Where(qs => qs.Query.ArchivedTime == null)
            .Select(qs => qs.Query)
            .DistinctBy(q => q.Id)
            .ToList();

        if (unarchivedQueries.Count > 0)
        {
            var queryNames = string.Join(", ", unarchivedQueries.Select(q => q.Name));
            throw new SemanticoException($"Unable to archive data source. The following queries must be archived first: {queryNames}");
        }

        // Check for non-archived migration jobs using this data source (as source or destination)
        var unarchivedMigrationJobs = await context.MigrationJobs
            .Where(mj => (mj.DataSourceId == dataSourceId || mj.DestinationDataSourceId == dataSourceId)
                         && mj.ArchivedTime == null)
            .ToListAsync(cancellationToken);

        if (unarchivedMigrationJobs.Count > 0)
        {
            var migrationJobNames = string.Join(", ", unarchivedMigrationJobs.Select(mj => mj.Name));
            throw new SemanticoException($"Unable to archive data source. The following migration jobs must be archived first: {migrationJobNames}");
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
//            .Where(x => x.ArchivedTime == null)
            .Select(x => new DataSourceListData
            {
                Id = x.Id,
                Name = x.Name,
                DatabaseEngineType = x.DatabaseEngineType,
                MigrationJobsCount = context.MigrationJobs
                    .Count(mj => (mj.DataSourceId == x.Id || mj.DestinationDataSourceId == x.Id)
                                 && mj.ArchivedTime == null),
                Queries = x.QuerySteps
                    .Where(qs => qs.Query.ArchivedTime == null)
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
        dataSource.ConnectionString = encryptionService.Encrypt(dataSourceData.ConnectionString);
        dataSource.DatabaseEngineType = dataSourceData.DatabaseEngineType;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AdHocQueryResult> ExecuteAdHocQuery(int dataSourceId, string query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new SemanticoException($"Data source with ID {dataSourceId} not found");

        var startTime = DateTime.UtcNow;

        try
        {
            var decryptedConnectionString = encryptionService.Decrypt(dataSource.ConnectionString);
            await using var connection = DbConnectionFactory.CreateConnection(dataSource.DatabaseEngineType, decryptedConnectionString);
            await connection.OpenAsync(cancellationToken);

            var result = await connection.QueryAsync(new CommandDefinition(query, cancellationToken: cancellationToken, commandTimeout: 120));
            var (columns, rows) = ConvertQueryResults(result.AsList());

            return new AdHocQueryResult
            {
                Success = true,
                Columns = columns,
                Rows = rows,
                RowCount = rows.Count,
                ExecutionTime = DateTime.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            return new AdHocQueryResult
            {
                Success = false,
                Error = ex.Message,
                ExecutionTime = DateTime.UtcNow - startTime,
                Columns = new List<string>(),
                Rows = new List<Dictionary<string, object?>>(),
                RowCount = 0
            };
        }
    }

    private (List<string> columns, List<Dictionary<string, object?>> rows) ConvertQueryResults(IList<dynamic> resultList)
    {
        if (resultList.Count == 0)
            return (new List<string>(), new List<Dictionary<string, object?>>());

        var firstRow = (IDictionary<string, object?>)resultList[0];
        var columns = firstRow.Keys.ToList();
        var rows = resultList
            .Select(item => ((IDictionary<string, object?>)item).ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
            .ToList();

        return (columns, rows);
    }
}
