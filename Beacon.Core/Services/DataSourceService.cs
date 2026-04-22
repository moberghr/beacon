using System.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Models.Providers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services.Providers;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services;

public interface IDataSourceService
{
    Task<BaseResponse> CreateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken);

    Task UpdateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken);

    Task DeleteDataSource(int dataSourceId, CancellationToken cancellationToken);

    Task<List<DataSourceListData>> GetDataSources(int? dataSourceId, CancellationToken cancellationToken);

    Task<AdHocQueryResult> ExecuteAdHocQuery(int dataSourceId, string query, CancellationToken cancellationToken);

    Task<BaseResponse> TestConnection(string connectionString, Data.Enums.DatabaseEngineType databaseEngineType, CancellationToken cancellationToken);

    Task<BaseResponse> TestConnectionAsync(DataSourceData dataSourceData, CancellationToken cancellationToken);

    Task<string> GetDecryptedConnectionData(int dataSourceId, CancellationToken cancellationToken);
}

internal class DataSourceService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    IDataSourceProviderFactory providerFactory,
    IManualQueryExecutionLogger queryExecutionLogger,
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
            DatabaseEngineType = dataSourceData.DatabaseEngineType,
            MetadataLoadingEnabled = dataSourceData.MetadataLoadingEnabled,
            MetadataMaxTables = dataSourceData.MetadataMaxTables,
            MetadataMaxColumnsPerTable = dataSourceData.MetadataMaxColumnsPerTable,
            MetadataLoadTableNamesOnly = dataSourceData.MetadataLoadTableNamesOnly,
            MetadataExcludeSchemas = dataSourceData.MetadataExcludeSchemas.Count > 0 ? string.Join(",", dataSourceData.MetadataExcludeSchemas) : null,
            MetadataIncludeSchemas = dataSourceData.MetadataIncludeSchemas.Count > 0 ? string.Join(",", dataSourceData.MetadataIncludeSchemas) : null,
        };

        context.DataSources.Add(dataSource);
        await context.SaveChangesAsync(cancellationToken);

        // For API data sources, import endpoint metadata from OpenAPI spec
        if (dataSourceData.DataSourceType == DataSourceType.Api)
        {
            try
            {
                var provider = providerFactory.GetProvider(DataSourceType.Api);
                var metadata = await provider.GetMetadataAsync(dataSource, cancellationToken);
                if (metadata.Endpoints?.Count > 0)
                {
                    await StoreApiEndpointsAsMetadata(context, dataSource.Id, metadata.Endpoints, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to import API metadata for data source {Id}. Can be retried via metadata refresh.", dataSource.Id);
            }
        }

        return new BaseResponse
        {
            Success = true
        };
    }

    private static async Task StoreApiEndpointsAsMetadata(
        BeaconContext context,
        int dataSourceId,
        List<ApiEndpointMetadata> endpoints,
        CancellationToken cancellationToken)
    {
        foreach (var endpoint in endpoints)
        {
            var schemaName = endpoint.Tag ?? "default";
            var tableName = $"{endpoint.Method} {endpoint.Path}";

            var metadata = new DatabaseMetadata
            {
                DataSourceId = dataSourceId,
                SchemaName = schemaName,
                TableName = tableName,
                TableDescription = endpoint.Summary ?? endpoint.Description,
                LastRefreshed = DateTime.UtcNow
            };

            // Add response fields as columns
            foreach (var (field, index) in endpoint.ResponseFields.Select((f, i) => (f, i)))
            {
                metadata.Columns.Add(new ColumnMetadata
                {
                    ColumnName = field.Name,
                    DataType = field.Type,
                    IsNullable = true,
                    OrdinalPosition = index,
                    Description = field.Description
                });
            }

            context.DatabaseMetadata.Add(metadata);
        }

        await context.SaveChangesAsync(cancellationToken);
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
            throw new BeaconException($"Unable to archive data source. The following queries must be archived first: {queryNames}");
        }

        // Check for non-archived migration jobs using this data source (as source or destination)
        var unarchivedMigrationJobs = await context.MigrationJobs
            .Where(mj => (mj.DataSourceId == dataSourceId || mj.DestinationDataSourceId == dataSourceId)
                         && mj.ArchivedTime == null)
            .ToListAsync(cancellationToken);

        if (unarchivedMigrationJobs.Count > 0)
        {
            var migrationJobNames = string.Join(", ", unarchivedMigrationJobs.Select(mj => mj.Name));
            throw new BeaconException($"Unable to archive data source. The following migration jobs must be archived first: {migrationJobNames}");
        }

        dataSource.Archive();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<DataSourceListData>> GetDataSources(int? dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Note: do not add a redundant `.Where(x => x.ArchivedTime == null)` —
        // the global soft-delete query filter already applies it, and combining the two on
        // EF Core 9 / Npgsql collapses the outer predicate to WHERE FALSE (returns nothing).
        return await context.DataSources
            .WhereIf(dataSourceId.HasValue, x => x.Id == dataSourceId)
            .Select(x => new DataSourceListData
            {
                Id = x.Id,
                Name = x.Name,
                DataSourceType = x.DataSourceType,
                DatabaseEngineType = x.DatabaseEngineType,
                MetadataLoadingEnabled = x.MetadataLoadingEnabled,
                ArchivedTime = x.ArchivedTime,
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
        dataSource.MetadataLoadingEnabled = dataSourceData.MetadataLoadingEnabled;
        dataSource.MetadataMaxTables = dataSourceData.MetadataMaxTables;
        dataSource.MetadataMaxColumnsPerTable = dataSourceData.MetadataMaxColumnsPerTable;
        dataSource.MetadataLoadTableNamesOnly = dataSourceData.MetadataLoadTableNamesOnly;
        dataSource.MetadataExcludeSchemas = dataSourceData.MetadataExcludeSchemas.Count > 0 ? string.Join(",", dataSourceData.MetadataExcludeSchemas) : null;
        dataSource.MetadataIncludeSchemas = dataSourceData.MetadataIncludeSchemas.Count > 0 ? string.Join(",", dataSourceData.MetadataIncludeSchemas) : null;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AdHocQueryResult> ExecuteAdHocQuery(int dataSourceId, string query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Data source with ID {dataSourceId} not found");

        var startTime = DateTime.UtcNow;

        try
        {
            // Add LIMIT/TOP clause to query if not already present (prevents fetching millions of rows)
            var limitedQuery = query;
            if (dataSource.DataSourceType == DataSourceType.Database && dataSource.DatabaseEngineType.HasValue)
            {
                limitedQuery = QueryLimitHelper.AddLimitIfMissing(
                    query,
                    dataSource.DatabaseEngineType.Value,
                    Constants.Query.MaxUiDisplayRows);

                if (limitedQuery != query)
                {
                    logger.LogInformation(
                        "Added automatic LIMIT clause to ad-hoc query for data source {DataSourceId} (engine: {Engine})",
                        dataSourceId, dataSource.DatabaseEngineType.Value);
                }
            }

            // Use provider factory to execute query for any data source type
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);

            logger.LogInformation("Executing ad-hoc query on data source {DataSourceId} ({DataSourceType})",
                dataSourceId, dataSource.DataSourceType);

            var result = await provider.ExecuteQueryAsync(
                dataSource,
                limitedQuery,  // Use limited query
                new Dictionary<string, object?>(),
                cancellationToken);

            if (!result.Success)
            {
                logger.LogWarning("Ad-hoc query failed for data source {DataSourceId}: {ErrorMessage}",
                    dataSourceId, result.ErrorMessage);
            }

            // Additional in-memory safety check (should not be needed if LIMIT worked)
            var displayRows = result.Rows.Take(Constants.Query.MaxUiDisplayRows).ToList();
            var totalRows = result.TotalRows;

            // Log manual query execution
            await queryExecutionLogger.LogQueryExecutionAsync(
                queryText: limitedQuery,
                resultCount: totalRows,
                executionTimeMs: result.ExecutionTimeMs,
                success: result.Success,
                dataSourceId: dataSourceId,
                executionContext: "DataSourceEditor",
                errorMessage: result.ErrorMessage,
                userId: null, // TODO: Set from middleware/user context
                cancellationToken: cancellationToken);

            return new AdHocQueryResult
            {
                Success = result.Success,
                Columns = result.Success ? ExtractColumns(displayRows) : new List<string>(),
                Rows = displayRows,
                RowCount = totalRows,
                ExecutionTime = TimeSpan.FromMilliseconds(result.ExecutionTimeMs),
                Error = result.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception executing ad-hoc query on data source {DataSourceId}", dataSourceId);

            var executionTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

            // Log failed query execution
            await queryExecutionLogger.LogQueryExecutionAsync(
                queryText: query,
                resultCount: 0,
                executionTimeMs: executionTimeMs,
                success: false,
                dataSourceId: dataSourceId,
                executionContext: "DataSourceEditor",
                errorMessage: ex.Message,
                userId: null, // TODO: Set from middleware/user context
                cancellationToken: cancellationToken);

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

    public async Task<BaseResponse> TestConnectionAsync(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        try
        {
            // For database types, use the connection factory directly
            if (dataSourceData.DataSourceType == DataSourceType.Database && dataSourceData.DatabaseEngineType.HasValue)
            {
                return await TestConnection(dataSourceData.ConnectionString, dataSourceData.DatabaseEngineType.Value, cancellationToken);
            }

            // For non-database types (CloudWatch, Databricks, BigQuery), use the provider pattern
            var provider = providerFactory.GetProvider(dataSourceData.DataSourceType);
            var tempDataSource = new DataSource
            {
                Name = dataSourceData.Name ?? "connection-test",
                DataSourceType = dataSourceData.DataSourceType,
                DatabaseEngineType = dataSourceData.DatabaseEngineType,
                EncryptedConnectionData = encryptionService.Encrypt(dataSourceData.ConnectionString)
            };

            var result = await provider.TestConnectionAsync(tempDataSource, cancellationToken);

            return new BaseResponse
            {
                Success = result.Success,
                Message = result.Success ? "Connection successful" : $"Connection failed: {result.ErrorMessage}"
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
            ?? throw new BeaconException($"Data source with ID {dataSourceId} not found");

        return encryptionService.Decrypt(dataSource.EncryptedConnectionData);
    }
}
