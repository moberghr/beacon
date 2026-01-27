using System.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.DataSources;
using Semantico.Core.Models.Queries;
using Semantico.Core.Services.Providers;
using Microsoft.Extensions.Logging;

namespace Semantico.Core.Services;

public interface IDataSourceService
{
    Task<BaseResponse> CreateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken);

    Task UpdateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken);

    Task DeleteDataSource(int dataSourceId, CancellationToken cancellationToken);

    Task<List<DataSourceListData>> GetDataSources(int? dataSourceId, CancellationToken cancellationToken);

    Task<AdHocQueryResult> ExecuteAdHocQuery(int dataSourceId, string query, CancellationToken cancellationToken);

    Task<BaseResponse> TestConnection(string connectionString, Data.Enums.DatabaseEngineType databaseEngineType, CancellationToken cancellationToken);

    Task<string> GetDecryptedConnectionData(int dataSourceId, CancellationToken cancellationToken);
}

internal class DataSourceService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IEncryptionService encryptionService,
    IDataSourceProviderFactory providerFactory,
    ILogger<DataSourceService> logger) : IDataSourceService
{
    public async Task<BaseResponse> CreateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = new DataSource
        {
            Name = dataSourceData.Name,
            DataSourceType = dataSourceData.DataSourceType,
            EncryptedConnectionData = encryptionService.Encrypt(dataSourceData.ConnectionString),
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
                DataSourceType = x.DataSourceType,
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
                            DataSourceType = x.DataSourceType,
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
        dataSource.EncryptedConnectionData = encryptionService.Encrypt(dataSourceData.ConnectionString);
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
            // Use provider factory to execute query for any data source type
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);

            logger.LogInformation("Executing ad-hoc query on data source {DataSourceId} ({DataSourceType})",
                dataSourceId, dataSource.DataSourceType);

            var result = await provider.ExecuteQueryAsync(
                dataSource,
                query,
                new Dictionary<string, object?>(),
                cancellationToken);

            if (!result.Success)
            {
                logger.LogWarning("Ad-hoc query failed for data source {DataSourceId}: {ErrorMessage}",
                    dataSourceId, result.ErrorMessage);
            }

            return new AdHocQueryResult
            {
                Success = result.Success,
                Columns = result.Success ? ExtractColumns(result.Rows) : new List<string>(),
                Rows = result.Rows,
                RowCount = result.TotalRows,
                ExecutionTime = TimeSpan.FromMilliseconds(result.ExecutionTimeMs),
                Error = result.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception executing ad-hoc query on data source {DataSourceId}", dataSourceId);

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

    private List<string> ExtractColumns(List<Dictionary<string, object?>> rows)
    {
        if (rows == null || rows.Count == 0)
            return new List<string>();

        return rows[0].Keys.ToList();
    }

    public async Task<BaseResponse> TestConnection(string connectionString, Data.Enums.DatabaseEngineType databaseEngineType, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = DbConnectionFactory.CreateConnection(databaseEngineType, connectionString);
            await connection.OpenAsync(cancellationToken);
            await connection.CloseAsync();

            return new BaseResponse
            {
                Success = true,
                Message = "Connection successful"
            };
        }
        catch (Exception ex)
        {
            return new BaseResponse
            {
                Success = false,
                Message = $"Connection failed: {ex.Message}"
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

    public async Task<string> GetDecryptedConnectionData(int dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new SemanticoException($"Data source with ID {dataSourceId} not found");

        return encryptionService.Decrypt(dataSource.EncryptedConnectionData);
    }
}
