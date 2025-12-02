using System.Data.Common;
using System.Dynamic;
using System.Text.Json;
using Dapper;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities.DataMigration;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Helpers.BulkHelpers;
using Semantico.Core.Models.DataMigration;
using Semantico.Core.Models.Queries;
using static Semantico.Core.Helpers.BulkExtension;

namespace Semantico.Core.Services;

public interface IMigrationService
{
    Task<CreateMigrationJobResponse> CreateMigrationJob(CreateMigrationJobRequest request, CancellationToken cancellationToken);
    Task<ExecuteMigrationJobResponse> ExecuteMigrationJob(ExecuteMigrationJobRequest request, CancellationToken cancellationToken);
    Task<PagedList<MigrationJobDto>> GetMigrationJobs(GetMigrationJobsRequest request, CancellationToken cancellationToken);
    Task<MigrationJobDetailsDto?> GetMigrationJob(int id, CancellationToken cancellationToken);
    Task<GetMigrationExecutionsResponse> GetMigrationExecutions(GetMigrationExecutionsRequest request, CancellationToken cancellationToken);
    Task<BaseResponse> UpdateMigrationJob(int id, CreateMigrationJobRequest request, CancellationToken cancellationToken);
    Task<BaseResponse> DeleteMigrationJob(int id, CancellationToken cancellationToken, bool forceDelete = false);
}

internal class MigrationService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IEncryptionService encryptionService,
    IQueryService queryService,
    IQueryExecutionPreviewService previewService,
    ILogger<MigrationService> logger) : IMigrationService
{
    public async Task<CreateMigrationJobResponse> CreateMigrationJob(CreateMigrationJobRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Validate request
            var validationResult = await ValidateMigrationJobRequest(request, cancellationToken);
            if (!validationResult.IsValid)
            {
                return new CreateMigrationJobResponse(0, false, "Validation failed", validationResult);
            }

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var migrationJob = new MigrationJob
            {
                Name = request.Name,
                Description = request.Description,
                DataSourceId = request.DataSourceId,
                QueryText = request.QueryText,
                DestinationDataSourceId = request.DestinationDataSourceId,
                DestinationTable = request.DestinationTable,
                Mode = request.Mode,
                IsEnabled = request.IsEnabled,
                Schedule = request.Schedule,
                MaxRetries = request.MaxRetries,
                TimeoutMinutes = request.TimeoutMinutes,
                ValidateBeforeExecution = request.ValidateBeforeExecution,
                TransformationScript = request.TransformationScript,
                ChangedBy = "System", // TODO: Get from current user context
                ChangedOn = DateTime.UtcNow
            };

            context.MigrationJobs.Add(migrationJob);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Created migration job {Name} with ID {Id}", request.Name, migrationJob.Id);

            return new CreateMigrationJobResponse(migrationJob.Id, true, null, validationResult);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating migration job {Name}", request.Name);
            return new CreateMigrationJobResponse(0, false, ex.Message);
        }
    }

    public async Task<ExecuteMigrationJobResponse> ExecuteMigrationJob(ExecuteMigrationJobRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var migrationJob = await context.MigrationJobs
                .Include(m => m.DataSource)
                .Include(m => m.DestinationDataSource)
                .FirstOrDefaultAsync(m => m.Id == request.MigrationJobId, cancellationToken);

            if (migrationJob == null)
            {
                return new ExecuteMigrationJobResponse(0, MigrationStatus.Failed, ErrorMessage: "Migration job not found");
            }

            if (!migrationJob.IsEnabled)
            {
                return new ExecuteMigrationJobResponse(0, MigrationStatus.Failed, ErrorMessage: "Migration job is disabled");
            }

            // Create execution record
            var execution = new MigrationExecutionHistory
            {
                MigrationJobId = migrationJob.Id,
                StartedAt = DateTime.UtcNow,
                Status = MigrationStatus.Running,
                ExecutedQuery = migrationJob.QueryText,
                QueryParameters = request.Parameters != null ? System.Text.Json.JsonSerializer.Serialize(request.Parameters) : null
            };

            context.MigrationExecutions.Add(execution);
            await context.SaveChangesAsync(cancellationToken);

            try
            {
                // Execute the migration using existing query service
                var migrationResult = await ExecuteMigrationInternal(migrationJob, execution, request, cancellationToken);
                
                // Update execution record
                execution.CompletedAt = DateTime.UtcNow;
                execution.Status = migrationResult.Status;
                execution.SourceRowsRead = migrationResult.SourceRowsRead;
                execution.DestinationRowsWritten = migrationResult.DestinationRowsWritten;
                execution.RowsSkipped = migrationResult.RowsSkipped;
                execution.RowsFailed = migrationResult.RowsFailed;
                execution.ErrorMessage = migrationResult.ErrorMessage;

                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Completed migration job {Name} execution {ExecutionId} with status {Status}", 
                    migrationJob.Name, execution.Id, execution.Status);

                return new ExecuteMigrationJobResponse(
                    execution.Id,
                    execution.Status,
                    execution.SourceRowsRead,
                    execution.DestinationRowsWritten,
                    execution.RowsSkipped,
                    execution.RowsFailed,
                    execution.ExecutionDuration,
                    execution.ErrorMessage,
                    migrationResult.Warnings ?? new List<string>()
                );
            }
            catch (Exception ex)
            {
                execution.CompletedAt = DateTime.UtcNow;
                execution.Status = MigrationStatus.Failed;
                execution.ErrorMessage = GetFullExceptionMessage(ex);
                await context.SaveChangesAsync(cancellationToken);

                logger.LogError(ex, "Failed migration job {Name} execution {ExecutionId}", migrationJob.Name, execution.Id);

                return new ExecuteMigrationJobResponse(execution.Id, MigrationStatus.Failed, ErrorMessage: GetFullExceptionMessage(ex));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing migration job {MigrationJobId}", request.MigrationJobId);
            return new ExecuteMigrationJobResponse(0, MigrationStatus.Failed, ErrorMessage: GetFullExceptionMessage(ex));
        }
    }

    public async Task<PagedList<MigrationJobDto>> GetMigrationJobs(GetMigrationJobsRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.MigrationJobs
            .Include(m => m.DataSource)
            .Include(m => m.DestinationDataSource)
            .Include(m => m.Executions)
            .AsQueryable();

        // Apply filters
        if (request.DataSourceId.HasValue)
        {
            query = query.Where(m => m.DataSourceId == request.DataSourceId.Value || m.DestinationDataSourceId == request.DataSourceId.Value);
        }

        if (request.IsEnabled.HasValue)
        {
            query = query.Where(m => m.IsEnabled == request.IsEnabled.Value);
        }

        // Note: Global query filter already handles archived records
        if (request.IncludeArchived)
        {
            query = query.IgnoreQueryFilters();
        }

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var searchTerm = request.SearchTerm.ToLower();
            query = query.Where(m => m.Name.ToLower().Contains(searchTerm) ||
                                   m.Description.ToLower().Contains(searchTerm));
        }

        var results = await query
            .Select(m => new MigrationJobDto(
                m.Id,
                m.Name,
                m.Description,
                m.DataSourceId,
                m.DataSource != null ? m.DataSource.Name : "Unknown",
                m.DestinationDataSourceId,
                m.DestinationDataSource != null ? m.DestinationDataSource.Name : "Unknown",
                m.DestinationTable,
                m.Mode,
                m.IsEnabled,
                m.Schedule,
                m.CreatedTime,
                m.Executions.OrderByDescending(e => e.StartedAt).FirstOrDefault() != null ?
                    m.Executions.OrderByDescending(e => e.StartedAt).First().StartedAt : null,
                m.Executions.OrderByDescending(e => e.StartedAt).FirstOrDefault() != null ?
                    m.Executions.OrderByDescending(e => e.StartedAt).First().Status : null,
                m.Executions.Count,
                m.Executions.Count(e => e.Status == MigrationStatus.Completed)
            ))
            .ToPagedListAsync(request, cancellationToken);
        
        return results;
    }

    public async Task<MigrationJobDetailsDto?> GetMigrationJob(int id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var job = await context.MigrationJobs
            .Where(m => m.Id == id)
            .Select(m => new MigrationJobDetailsDto(
                m.Id,
                m.Name,
                m.Description,
                m.DataSourceId,
                m.QueryText,
                m.DestinationDataSourceId,
                m.DestinationTable,
                m.Mode,
                m.IsEnabled,
                m.Schedule,
                m.MaxRetries,
                m.TimeoutMinutes,
                m.ValidateBeforeExecution,
                m.TransformationScript
            ))
            .FirstOrDefaultAsync(cancellationToken);

        return job;
    }

    public async Task<GetMigrationExecutionsResponse> GetMigrationExecutions(GetMigrationExecutionsRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.MigrationExecutions
            .Include(e => e.MigrationJob)
            .AsQueryable();

        // Apply filters
        if (request.MigrationJobId.HasValue)
        {
            query = query.Where(e => e.MigrationJobId == request.MigrationJobId.Value);
        }

        if (request.Status.HasValue)
        {
            query = query.Where(e => e.Status == request.Status.Value);
        }

        if (request.StartDate.HasValue)
        {
            query = query.Where(e => e.StartedAt >= request.StartDate.Value);
        }

        if (request.EndDate.HasValue)
        {
            query = query.Where(e => e.StartedAt <= request.EndDate.Value);
        }

        // Apply sorting
        query = request.SortBy switch
        {
            MigrationExecutionSortBy.StartedAt => request.SortDescending ? 
                query.OrderByDescending(e => e.StartedAt) : query.OrderBy(e => e.StartedAt),
            MigrationExecutionSortBy.CompletedAt => request.SortDescending ? 
                query.OrderByDescending(e => e.CompletedAt) : query.OrderBy(e => e.CompletedAt),
            MigrationExecutionSortBy.Status => request.SortDescending ? 
                query.OrderByDescending(e => e.Status) : query.OrderBy(e => e.Status),
            MigrationExecutionSortBy.RowsProcessed => request.SortDescending ? 
                query.OrderByDescending(e => e.SourceRowsRead) : query.OrderBy(e => e.SourceRowsRead),
            _ => query.OrderByDescending(e => e.StartedAt)
        };

        var totalCount = await query.CountAsync(cancellationToken);

        var executions = await query
            .Skip(request.Skip)
            .Take(request.Take)
            .Select(e => new MigrationExecutionDto(
                e.Id,
                e.MigrationJobId,
                e.MigrationJob.Name,
                e.StartedAt,
                e.CompletedAt,
                e.Status,
                e.SourceRowsRead,
                e.DestinationRowsWritten,
                e.RowsSkipped,
                e.RowsFailed,
                e.ExecutionDuration,
                e.RowsPerSecond,
                e.ErrorMessage,
                e.RetryAttempt,
                e.ParentExecutionId.HasValue
            ))
            .ToListAsync(cancellationToken);

        return new GetMigrationExecutionsResponse(executions, totalCount, totalCount > request.Skip + request.Take);
    }

    public async Task<BaseResponse> UpdateMigrationJob(int id, CreateMigrationJobRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var migrationJob = await context.MigrationJobs.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
            if (migrationJob == null)
            {
                return new BaseResponse { Success = false, Message = "Migration job not found" };
            }

            // Update properties
            migrationJob.Name = request.Name;
            migrationJob.Description = request.Description;
            migrationJob.DataSourceId = request.DataSourceId;
            migrationJob.QueryText = request.QueryText;
            migrationJob.DestinationDataSourceId = request.DestinationDataSourceId;
            migrationJob.DestinationTable = request.DestinationTable;
            migrationJob.Mode = request.Mode;
            migrationJob.IsEnabled = request.IsEnabled;
            migrationJob.Schedule = request.Schedule;
            migrationJob.MaxRetries = request.MaxRetries;
            migrationJob.TimeoutMinutes = request.TimeoutMinutes;
            migrationJob.ValidateBeforeExecution = request.ValidateBeforeExecution;
            migrationJob.TransformationScript = request.TransformationScript;
            migrationJob.ChangedBy = "System"; // TODO: Get from current user context
            migrationJob.ChangedOn = DateTime.UtcNow;

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Updated migration job {Id} ({Name})", id, request.Name);

            return new BaseResponse { Success = true };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating migration job {Id}", id);
            return new BaseResponse { Success = false, Message = ex.Message };
        }
    }

    public async Task<BaseResponse> DeleteMigrationJob(int id, CancellationToken cancellationToken, bool forceDelete = false)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var migrationJob = await context.MigrationJobs.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
            if (migrationJob == null)
            {
                return new BaseResponse { Success = false, Message = "Migration job not found" };
            }

            if (forceDelete)
            {
                context.MigrationJobs.Remove(migrationJob);
            }
            else
            {
                migrationJob.Archive();
            }

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Deleted migration job {Id} (ForceDelete: {ForceDelete})", id, forceDelete);

            return new BaseResponse { Success = true };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting migration job {Id}", id);
            return new BaseResponse { Success = false, Message = ex.Message };
        }
    }

    private async Task<ValidationResult> ValidateMigrationJobRequest(CreateMigrationJobRequest request, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        
        // Basic validation
        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add("Name is required");
        
        if (string.IsNullOrWhiteSpace(request.QueryText))
            errors.Add("Query text is required");
        
        if (string.IsNullOrWhiteSpace(request.DestinationTable))
            errors.Add("Destination table is required");

        // TODO: Add more sophisticated validation
        // - Validate cron expression
        // - Validate query syntax
        // - Validate destination connectivity
        // - Check if projects exist and are accessible

        var queryValidated = !string.IsNullOrWhiteSpace(request.QueryText);
        var destinationValidated = !string.IsNullOrWhiteSpace(request.DestinationTable);

        return new ValidationResult(!errors.Any(), errors, queryValidated, destinationValidated);
    }

    private async Task<ExecuteMigrationJobResponse> ExecuteMigrationInternal(
        MigrationJob migrationJob, 
        MigrationExecutionHistory execution, 
        ExecuteMigrationJobRequest request, 
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        
        try
        {
            // Parse query steps from JSON format
            var querySteps = ParseQueryStepsFromJson(migrationJob.QueryText);
            
            if (!querySteps.Any())
            {
                throw new InvalidOperationException("No query steps found in migration job");
            }

            // Create temporary query data for execution
            var queryData = new QueryData
            {
                Name = $"Migration-{migrationJob.Name}",
                Description = migrationJob.Description,
                Steps = querySteps
            };

            // Execute the query to get source data using preview service
            var parameters = request.Parameters?.Select(kvp => new ParameterValue 
            { 
                Name = kvp.Key, 
                Value = kvp.Value?.ToString() ?? ""
            }).ToList() ?? new List<ParameterValue>();

            var queryResult = await previewService.ExecuteTemporaryQueryPreview(queryData, cancellationToken);
            
            if (!queryResult.Success || queryResult.FinalResult?.AllRecords == null)
            {
                throw new InvalidOperationException($"Source query execution failed: {queryResult.ErrorMessage}");
            }

            var sourceData = queryResult.FinalResult.AllRecords;
            var sourceRowsRead = sourceData.Count;

            // Get destination data source for data insertion
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var destinationDataSource = await context.DataSources
                .FirstOrDefaultAsync(ds => ds.Id == migrationJob.DestinationDataSourceId, cancellationToken);

            if (destinationDataSource == null)
            {
                throw new InvalidOperationException("Destination data source not found");
            }

            // Convert to concrete Dictionary type and apply transformation if specified
            var sourceDataDict = sourceData.Select(d => d.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)).ToList();
            var transformedData = ApplyTransformation(sourceDataDict, migrationJob.TransformationScript);

            // Execute destination insert based on migration mode
            var (rowsWritten, rowsSkipped, rowsFailed, errorDetails) = await ExecuteDestinationOperation(
                destinationDataSource,
                migrationJob.DestinationTable,
                transformedData,
                migrationJob.Mode,
                cancellationToken);

            var status = rowsFailed > 0 ? MigrationStatus.PartialSuccess : MigrationStatus.Completed;

            // Build error message for partial success scenarios
            string? errorMessage = null;
            if (status == MigrationStatus.PartialSuccess)
            {
                var successSummary = $"Successfully processed {rowsWritten} of {sourceRowsRead} rows";
                if (rowsSkipped > 0)
                    successSummary += $", skipped {rowsSkipped} rows";

                var failureSummary = $"Failed to process {rowsFailed} rows";

                var detailedErrors = errorDetails.Any()
                    ? $"\n\nDetailed errors:\n{string.Join("\n", errorDetails)}"
                    : "";

                errorMessage = $"{successSummary}. {failureSummary}.{detailedErrors}";
            }

            logger.LogInformation("Migration execution completed: {SourceRows} read, {DestinationRows} written, {Skipped} skipped, {Failed} failed",
                sourceRowsRead, rowsWritten, rowsSkipped, rowsFailed);

            return new ExecuteMigrationJobResponse(
                execution.Id,
                status,
                sourceRowsRead,
                rowsWritten,
                rowsSkipped,
                rowsFailed,
                TimeSpan.FromMilliseconds(queryResult.TotalExecutionTimeMs),
                errorMessage,
                warnings
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration execution failed");
            throw;
        }
    }

    private List<QueryStepData> ParseQueryStepsFromJson(string queryTextJson)
    {
        if (string.IsNullOrWhiteSpace(queryTextJson))
            return new List<QueryStepData>();

        try
        {
            // Try to parse as JSON first (new format)
            var stepObjects = JsonSerializer.Deserialize<List<JsonElement>>(queryTextJson);
            return stepObjects?.Select((step, index) => new QueryStepData
            {
                StepId = 0, // Temporary step
                StepOrder = step.TryGetProperty("StepOrder", out var stepOrder) ? stepOrder.GetInt32() : index + 1,
                Name = step.TryGetProperty("Name", out var name) ? name.GetString() : $"Step {index + 1}",
                Description = step.TryGetProperty("Description", out var desc) ? desc.GetString() : null,
                SqlValue = step.TryGetProperty("SqlValue", out var sql) ? sql.GetString() ?? "" : "",
                DataSourceId = step.TryGetProperty("DataSourceId", out var dataSourceId) ? dataSourceId.GetInt32() : 0,
                DataSourceName = step.TryGetProperty("DataSourceName", out var dataSourceName) ? dataSourceName.GetString() : "",
                DatabaseEngineType = DatabaseEngineType.PostgreSQL, // Default
                Parameters = new List<QueryStepParameterData>()
            }).ToList() ?? new List<QueryStepData>();
        }
        catch
        {
            // Fallback: treat as plain SQL (old format)
            return new List<QueryStepData>
            {
                new QueryStepData
                {
                    StepId = 0,
                    StepOrder = 1,
                    Name = "Migration Query",
                    Description = "Data extraction query",
                    SqlValue = queryTextJson,
                    DataSourceId = 0, // Will need to be resolved
                    DataSourceName = "Unknown",
                    DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                    Parameters = new List<QueryStepParameterData>()
                }
            };
        }
    }

    private List<Dictionary<string, object?>> ApplyTransformation(List<Dictionary<string, object?>> sourceData, string? transformationScript)
    {
        if (string.IsNullOrWhiteSpace(transformationScript))
            return sourceData;

        // TODO: Implement transformation logic using @result syntax
        // For now, return data as-is
        return sourceData;
    }

    private async Task<(int rowsWritten, int rowsSkipped, int rowsFailed, List<string> errorDetails)> ExecuteDestinationOperation(
        Data.Entities.DataSource destinationDataSource,
        string destinationTable,
        List<Dictionary<string, object?>> data,
        MigrationMode mode,
        CancellationToken cancellationToken)
    {
        DbConnection? connection = null;
        DbTransaction? transaction = null;

        try
        {
            // Create database connection based on engine type
            connection = CreateDatabaseConnection(destinationDataSource);
            await connection.OpenAsync(cancellationToken);

            // Validate destination table exists
            await ValidateDestinationTable(connection, destinationTable, destinationDataSource.DatabaseEngineType);

            // Start transaction for data consistency
            transaction = await connection.BeginTransactionAsync(cancellationToken);

            var rowsWritten = 0;
            var rowsSkipped = 0;
            var rowsFailed = 0;
            var errorDetails = new List<string>();

            try
            {
                // Execute operation based on mode
                switch (mode)
                {
                    case MigrationMode.Truncate:
                        await ExecuteTruncate(connection, transaction, destinationTable, destinationDataSource.DatabaseEngineType);
                        (rowsWritten, rowsFailed, errorDetails) = await ExecuteInserts(connection, transaction, destinationTable, data, destinationDataSource.DatabaseEngineType);
                        break;

                    case MigrationMode.Insert:
                        (rowsWritten, rowsFailed, errorDetails) = await ExecuteInserts(connection, transaction, destinationTable, data, destinationDataSource.DatabaseEngineType);
                        break;

                    case MigrationMode.Upsert:
                        (rowsWritten, rowsFailed, errorDetails) = await ExecuteUpserts(connection, transaction, destinationTable, data, destinationDataSource.DatabaseEngineType);
                        break;
                }

                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation("Destination operation completed for table {Table}: {Mode} mode, {Written} written, {Skipped} skipped, {Failed} failed",
                    destinationTable, mode, rowsWritten, rowsSkipped, rowsFailed);

                if (errorDetails.Any())
                {
                    logger.LogWarning("Migration completed with errors: {Errors}", string.Join("; ", errorDetails));
                }

                return (rowsWritten, rowsSkipped, rowsFailed, errorDetails);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException($"Failed to execute {mode} operation on table '{destinationTable}': {ex.Message}", ex);
            }
        }
        catch (Exception ex) when (ex.Message.Contains("does not exist"))
        {
            throw new InvalidOperationException($"Destination table '{destinationTable}' does not exist in database '{destinationDataSource.Name}'", ex);
        }
        catch (Exception ex) when (ex.Message.Contains("connect") || ex.Message.Contains("connection"))
        {
            throw new InvalidOperationException($"Unable to connect to destination database '{destinationDataSource.Name}': {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Database operation failed for table '{destinationTable}': {ex.Message}", ex);
        }
        finally
        {
            transaction?.Dispose();
            if (connection != null)
            {
                await connection.CloseAsync();
                await connection.DisposeAsync();
            }
        }
    }

    private DbConnection CreateDatabaseConnection(Data.Entities.DataSource dataSource)
    {
        try
        {
            var connectionString = encryptionService.Decrypt(dataSource.ConnectionString);
            return DbConnectionFactory.CreateConnection(dataSource.DatabaseEngineType, connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create database connection for data source '{dataSource.Name}': {ex.Message}", ex);
        }
    }

    private async Task ValidateDestinationTable(DbConnection connection, string tableName, DatabaseEngineType engineType)
    {
        try
        {
            // Parse schema and table name
            var (schema, table) = ParseSchemaAndTableName(tableName);

            var checkQuery = engineType switch
            {
                DatabaseEngineType.PostgreSQL => schema != null
                    ? $"SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_schema = '{schema}' AND table_name = '{table}')"
                    : $"SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = '{table}')",
                DatabaseEngineType.MySQL => schema != null
                    ? $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '{schema}' AND table_name = '{table}'"
                    : $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{table}' AND table_schema = DATABASE()",
                DatabaseEngineType.MSSQL => schema != null
                    ? $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{table}'"
                    : $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{table}'",
                _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
            };

            var exists = await connection.ExecuteScalarAsync<bool>(checkQuery);
            if (!exists)
            {
                throw new InvalidOperationException($"Table '{tableName}' does not exist in the destination database");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to validate destination table '{tableName}': {ex.Message}", ex);
        }
    }

    private (string? schema, string table) ParseSchemaAndTableName(string tableName)
    {
        // Handle schema-qualified table names (e.g., "schema.table")
        var parts = tableName.Split('.', 2);
        if (parts.Length == 2)
        {
            return (parts[0], parts[1]);
        }
        return (null, tableName);
    }

    private async Task ExecuteTruncate(DbConnection connection, DbTransaction transaction, string tableName, DatabaseEngineType engineType)
    {
        try
        {
            var truncateQuery = $"TRUNCATE TABLE {tableName}";
            await connection.ExecuteAsync(truncateQuery, transaction: transaction);
            logger.LogInformation("Truncated table {Table}", tableName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to truncate table '{tableName}': {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteInserts(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        DatabaseEngineType engineType)
    {
        var errorDetails = new List<string>();

        if (!data.Any())
            return (0, 0, errorDetails);

        // Use database-specific bulk insert methods
        if (engineType == DatabaseEngineType.PostgreSQL)
        {
            return await ExecutePostgresInsert(connection, transaction, tableName, data, errorDetails);
        }
        else if (engineType == DatabaseEngineType.MSSQL)
        {
            return await ExecuteSqlServerInsert(connection, tableName, data, errorDetails);
        }
        else
        {
            return await ExecuteGenericBulkInsert(connection, transaction, tableName, data, engineType, errorDetails);
        }

    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteUpserts(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        DatabaseEngineType engineType)
    {
        var errorDetails = new List<string>();

        if (!data.Any())
            return (0, 0, errorDetails);

        try
        {
            // Get primary key columns for the table
            var primaryKeyColumns = await GetPrimaryKeyColumns(connection, tableName, engineType);

            if (!primaryKeyColumns.Any())
            {
                logger.LogWarning("No primary key found for table {Table}, falling back to insert mode", tableName);
                return await ExecuteInserts(connection, transaction, tableName, data, engineType);
            }

            // Validate that all rows have the primary key columns
            var missingKeys = data.Where(row => !primaryKeyColumns.All(pk => row.ContainsKey(pk))).ToList();
            if (missingKeys.Any())
            {
                var errorMsg = $"Some rows are missing primary key columns: {string.Join(", ", primaryKeyColumns)}";
                logger.LogError(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            // Use database-specific bulk upsert methods
            if (engineType == DatabaseEngineType.PostgreSQL)
            {
                return await ExecutePostgresUpsert(connection, transaction, tableName, data, primaryKeyColumns, errorDetails);
            }
            else if (engineType == DatabaseEngineType.MSSQL)
            {
                return await ExecuteSqlServerUpsert(connection, tableName, data, errorDetails);
            }

            // Use temp table + merge approach for other databases (MySQL)
            var tempTableName = $"temp_{Guid.NewGuid():N}";

            // Filter out any empty column names
            var columns = data.First().Keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();

            if (!columns.Any())
            {
                throw new InvalidOperationException("Source data contains no valid column names");
            }

            try
            {
                // Step 1: Create temp table with same structure as destination
                logger.LogDebug("Creating temp table {TempTable} from {SourceTable}", tempTableName, tableName);
                await CreateTempTable(connection, transaction, tempTableName, tableName, engineType);
                logger.LogDebug("Temp table created successfully");

                // Step 2: Bulk insert data into temp table using PhenX
                // Filter data to only include valid columns
                var filteredData = data.Select(row =>
                    row.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                       .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                ).ToList();

                await using var bulkContext = CreateBulkContextWithConnection(connection, transaction, engineType, tempTableName, null);
                var entities = ConvertToExpandoObjects(filteredData);

                logger.LogDebug("Bulk inserting {RowCount} rows into temp table {TempTable}", entities.Count, tempTableName);

                var bulkConfig = new BulkConfig
                {
                    SetOutputIdentity = false,
                    BulkCopyTimeout = Constants.Migration.BulkCopyTimeoutSeconds,
                    BatchSize = Constants.Migration.UpsertBatchSize,
                    CustomDestinationTableName = tempTableName,
                    UseTempDB = false
                };

                await bulkContext.BulkInsertAsync(entities.Cast<object>().ToList(), bulkConfig);

                // Step 3: Merge from temp table to destination table
                var mergeQuery = BuildMergeQuery(tableName, tempTableName, columns, primaryKeyColumns, engineType);

                logger.LogDebug("Executing merge query: {MergeQuery}", mergeQuery);

                try
                {
                    await connection.ExecuteAsync(mergeQuery, transaction: transaction);
                }
                catch (Exception mergeEx)
                {
                    logger.LogError(mergeEx, "Merge query failed. Query: {Query}", mergeQuery);
                    throw new InvalidOperationException($"Merge query failed: {mergeEx.Message}. Query: {mergeQuery}", mergeEx);
                }

                logger.LogInformation("Bulk upserted {RowCount} rows into {Table} via temp table", data.Count, tableName);
                return (data.Count, 0, errorDetails);
            }
            finally
            {
                // Clean up temp table
                try
                {
                    if (engineType != DatabaseEngineType.PostgreSQL) // PostgreSQL auto-drops temp tables
                    {
                        await connection.ExecuteAsync($"DROP TABLE IF EXISTS {tempTableName}", transaction: transaction);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Failed to drop temp table {TempTable}: {Error}", tempTableName, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Upsert operation failed: {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecutePostgresInsert(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        List<string> errorDetails)
    {
        try
        {
            var npgsqlConnection = connection as Npgsql.NpgsqlConnection;
            if (npgsqlConnection == null)
            {
                throw new InvalidOperationException("Connection is not a PostgreSQL connection");
            }

            var columns = data.First().Keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            var quotedColumns = string.Join(", ", columns.Select(c => $"\"{c}\""));

            // Use PostgreSQL COPY for fast bulk insert
            var copyCommand = $"COPY {tableName} ({quotedColumns}) FROM STDIN (FORMAT BINARY)";

            await using var import = await npgsqlConnection.BeginBinaryImportAsync(copyCommand);

            foreach (var row in data)
            {
                await import.StartRowAsync();
                foreach (var col in columns)
                {
                    var value = row.ContainsKey(col) ? row[col] : null;
                    await import.WriteAsync(value);
                }
            }

            await import.CompleteAsync();
            var rowsWritten = data.Count; // COPY doesn't return rows imported, use data count

            logger.LogInformation("Bulk inserted {RowCount} rows into {Table} using PostgreSQL COPY", rowsWritten, tableName);
            return (rowsWritten, 0, errorDetails);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PostgreSQL bulk insert failed for table {Table}", tableName);
            throw new InvalidOperationException($"PostgreSQL bulk insert failed: {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteSqlServerInsert(
        DbConnection connection,
        string tableName,
        List<Dictionary<string, object?>> data,
        List<string> errorDetails)
    {
        try
        {
            var (schema, table) = ParseSchemaAndTableName(tableName);
            var entities = ConvertToExpandoObjects(data);

            // Get connection string from the connection
            var connectionString = connection.ConnectionString;

            using var dataTransferManager = new SqlServerDataTransferManager(connectionString);
            dataTransferManager.BulkInsert(entities, table, schema);

            logger.LogInformation("Bulk inserted {RowCount} rows into {Table} using SqlServerDataTransferManager", data.Count, tableName);
            return await Task.FromResult((data.Count, 0, errorDetails));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SQL Server bulk insert failed for table {Table}", tableName);
            throw new InvalidOperationException($"SQL Server bulk insert failed: {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecutePostgresUpsert(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        List<string> primaryKeyColumns,
        List<string> errorDetails)
    {
        try
        {
            var npgsqlConnection = connection as Npgsql.NpgsqlConnection;
            if (npgsqlConnection == null)
            {
                throw new InvalidOperationException("Connection is not a PostgreSQL connection");
            }

            var columns = data.First().Keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            var tempTableName = $"temp_{Guid.NewGuid():N}";

            // Step 1: Create temp table
            var quotedColumns = string.Join(", ", columns.Select(c => $"\"{c}\""));
            var createTempQuery = $"CREATE TEMP TABLE {tempTableName} AS SELECT {quotedColumns} FROM {tableName} LIMIT 0";
            await connection.ExecuteAsync(createTempQuery, transaction: transaction);

            // Step 2: Bulk insert into temp table using COPY
            var copyCommand = $"COPY {tempTableName} ({quotedColumns}) FROM STDIN (FORMAT BINARY)";
            await using (var import = await npgsqlConnection.BeginBinaryImportAsync(copyCommand))
            {
                foreach (var row in data)
                {
                    await import.StartRowAsync();
                    foreach (var col in columns)
                    {
                        var value = row.ContainsKey(col) ? row[col] : null;
                        await import.WriteAsync(value);
                    }
                }

                await import.CompleteAsync();
            }

            // Step 3: Merge from temp table to destination
            var nonKeyColumns = columns.Except(primaryKeyColumns).ToList();
            var quotedPrimaryKeys = string.Join(", ", primaryKeyColumns.Select(pk => $"\"{pk}\""));
            var updateSet = nonKeyColumns.Any()
                ? string.Join(", ", nonKeyColumns.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))
                : $"\"{primaryKeyColumns.First()}\" = EXCLUDED.\"{primaryKeyColumns.First()}\"";

            var mergeQuery = $@"
INSERT INTO {tableName} ({quotedColumns})
SELECT {quotedColumns} FROM {tempTableName}
ON CONFLICT ({quotedPrimaryKeys})
DO UPDATE SET {updateSet}";

            await connection.ExecuteAsync(mergeQuery, transaction: transaction);

            logger.LogInformation("Bulk upserted {RowCount} rows into {Table} using PostgreSQL COPY + MERGE", data.Count, tableName);
            return (data.Count, 0, errorDetails);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PostgreSQL bulk upsert failed for table {Table}", tableName);
            throw new InvalidOperationException($"PostgreSQL bulk upsert failed: {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteSqlServerUpsert(
        DbConnection connection,
        string tableName,
        List<Dictionary<string, object?>> data,
        List<string> errorDetails)
    {
        try
        {
            var (schema, table) = ParseSchemaAndTableName(tableName);
            var entities = ConvertToExpandoObjects(data);

            // Get connection string from the connection
            var connectionString = connection.ConnectionString;

            using var dataTransferManager = new SqlServerDataTransferManager(connectionString);

            // Use MergeData for upsert - overwriteDestination=false, updateOnlyChangedRows=true
            dataTransferManager.MergeData(entities, table, schema, overwriteDestination: false, updateOnlyChangedRows: true);

            logger.LogInformation("Bulk upserted {RowCount} rows into {Table} using SqlServerDataTransferManager", data.Count, tableName);
            return await Task.FromResult((data.Count, 0, errorDetails));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SQL Server bulk upsert failed for table {Table}", tableName);
            throw new InvalidOperationException($"SQL Server bulk upsert failed: {ex.Message}", ex);
        }
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteGenericBulkInsert(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        DatabaseEngineType engineType,
        List<string> errorDetails)
    {
        try
        {
            // Use EFCore.BulkExtensions for other databases
            await using var bulkContext = CreateBulkContextWithConnection(connection, transaction, engineType, tableName, null);

            var entities = ConvertToExpandoObjects(data);

            var bulkConfig = new BulkConfig
            {
                SetOutputIdentity = false,
                BulkCopyTimeout = Constants.Migration.BulkCopyTimeoutSeconds,
                BatchSize = Constants.Migration.BulkInsertBatchSize,
                CustomDestinationTableName = tableName,
                UseTempDB = false
            };

            await bulkContext.BulkInsertAsync(entities.Cast<object>().ToList(), bulkConfig);

            logger.LogInformation("Bulk inserted {RowCount} rows into {Table}", data.Count, tableName);
            return (data.Count, 0, errorDetails);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bulk insert failed for table {Table}, falling back to row-by-row", tableName);

            // Fallback to row-by-row insertion on bulk failure
            var rowsWritten = 0;
            var rowsFailed = 0;

            foreach (var row in data)
            {
                try
                {
                    var columns = string.Join(", ", row.Keys);
                    var parameters = string.Join(", ", row.Keys.Select(k => $"@{k}"));
                    var insertQuery = $"INSERT INTO {tableName} ({columns}) VALUES ({parameters})";

                    await connection.ExecuteAsync(insertQuery, row, transaction);
                    rowsWritten++;
                }
                catch (Exception rowEx)
                {
                    rowsFailed++;
                    var errorMsg = $"Row {rowsWritten + rowsFailed} failed: {rowEx.Message}";
                    errorDetails.Add(errorMsg);
                    logger.LogWarning("Failed to insert row into {Table}: {Error}", tableName, rowEx.Message);

                    if (rowsFailed > Constants.Migration.MaxFailedRowsBeforeStop)
                    {
                        errorDetails.Add($"Too many errors (>{Constants.Migration.MaxFailedRowsBeforeStop}), stopping insertion");
                        break;
                    }
                }
            }

            return (rowsWritten, rowsFailed, errorDetails);
        }
    }

    private DynamicDbContext CreateBulkContextWithConnection(DbConnection connection, DbTransaction transaction, DatabaseEngineType engineType, string? tableName = null, List<string>? primaryKeys = null)
    {
        var options = engineType switch
        {
            DatabaseEngineType.PostgreSQL => new DbContextOptionsBuilder<DynamicDbContext>()
                .UseNpgsql(connection)
                .Options,
            DatabaseEngineType.MySQL => new DbContextOptionsBuilder<DynamicDbContext>()
                .UseMySql(connection, ServerVersion.AutoDetect(connection.ConnectionString))
                .Options,
            DatabaseEngineType.MSSQL => new DbContextOptionsBuilder<DynamicDbContext>()
                .UseSqlServer(connection)
                .Options,
            _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
        };

        var context = new DynamicDbContext(options, tableName, primaryKeys);
        context.Database.UseTransaction(transaction);
        return context;
    }

    private List<ExpandoObject> ConvertToExpandoObjects(List<Dictionary<string, object?>> data)
    {
        var result = new List<ExpandoObject>();

        foreach (var row in data)
        {
            var expando = new ExpandoObject();
            var expandoDict = (IDictionary<string, object?>)expando;

            foreach (var kvp in row)
            {
                expandoDict[kvp.Key] = kvp.Value;
            }

            result.Add(expando);
        }

        return result;
    }

    private async Task CreateTempTable(DbConnection connection, DbTransaction transaction, string tempTableName, string sourceTableName, DatabaseEngineType engineType)
    {
        var createQuery = engineType switch
        {
            // PostgreSQL: Don't quote temp table name - let it be lowercase
            DatabaseEngineType.PostgreSQL => $"CREATE TEMP TABLE {tempTableName} AS SELECT * FROM {sourceTableName} LIMIT 0",
            DatabaseEngineType.MySQL => $"CREATE TEMPORARY TABLE {tempTableName} LIKE {sourceTableName}",
            DatabaseEngineType.MSSQL => $"SELECT * INTO {tempTableName} FROM {sourceTableName} WHERE 1=0",
            _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
        };

        try
        {
            await connection.ExecuteAsync(createQuery, transaction: transaction);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create temp table with query: {createQuery}. Error: {ex.Message}", ex);
        }
    }

    private string BuildMergeQuery(string destinationTable, string sourceTable, List<string> columns, List<string> primaryKeyColumns, DatabaseEngineType engineType)
    {
        var nonKeyColumns = columns.Except(primaryKeyColumns).ToList();

        return engineType switch
        {
            DatabaseEngineType.PostgreSQL => BuildPostgreSqlMerge(destinationTable, sourceTable, columns, primaryKeyColumns, nonKeyColumns),
            DatabaseEngineType.MySQL => BuildMySqlMerge(destinationTable, sourceTable, columns, primaryKeyColumns, nonKeyColumns),
            DatabaseEngineType.MSSQL => BuildSqlServerMerge(destinationTable, sourceTable, columns, primaryKeyColumns, nonKeyColumns),
            _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
        };
    }

    private string BuildPostgreSqlMerge(string destinationTable, string sourceTable, List<string> columns, List<string> primaryKeyColumns, List<string> nonKeyColumns)
    {
        // Quote identifiers to handle case-sensitive and reserved words
        var quotedColumns = columns.Select(c => $"\"{c}\"").ToList();
        var columnList = string.Join(", ", quotedColumns);
        var quotedPrimaryKeys = primaryKeyColumns.Select(pk => $"\"{pk}\"").ToList();

        var updateSet = nonKeyColumns.Any()
            ? string.Join(", ", nonKeyColumns.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))
            : $"\"{primaryKeyColumns.First()}\" = EXCLUDED.\"{primaryKeyColumns.First()}\""; // Dummy update if no non-key columns

        // Quote table names properly
        var quotedSourceTable = sourceTable.Contains(".") ? sourceTable : $"\"{sourceTable}\"";

        return $@"
INSERT INTO {destinationTable} ({columnList})
SELECT {columnList} FROM {quotedSourceTable}
ON CONFLICT ({string.Join(", ", quotedPrimaryKeys)})
DO UPDATE SET {updateSet}";
    }

    private string BuildMySqlMerge(string destinationTable, string sourceTable, List<string> columns, List<string> primaryKeyColumns, List<string> nonKeyColumns)
    {
        var columnList = string.Join(", ", columns);
        var sourceColumns = string.Join(", ", columns.Select(c => $"s.{c}"));
        var updateSet = nonKeyColumns.Any()
            ? string.Join(", ", nonKeyColumns.Select(c => $"{c} = VALUES({c})"))
            : primaryKeyColumns.First() + " = " + primaryKeyColumns.First(); // Dummy update

        return $@"
            INSERT INTO {destinationTable} ({columnList})
            SELECT {columnList} FROM {sourceTable}
            ON DUPLICATE KEY UPDATE {updateSet}";
    }

    private string BuildSqlServerMerge(string destinationTable, string sourceTable, List<string> columns, List<string> primaryKeyColumns, List<string> nonKeyColumns)
    {
        var keyConditions = string.Join(" AND ", primaryKeyColumns.Select(pk => $"target.{pk} = source.{pk}"));
        var insertColumns = string.Join(", ", columns);
        var insertValues = string.Join(", ", columns.Select(c => $"source.{c}"));
        var updateSet = nonKeyColumns.Any()
            ? string.Join(", ", nonKeyColumns.Select(c => $"target.{c} = source.{c}"))
            : $"target.{primaryKeyColumns.First()} = source.{primaryKeyColumns.First()}"; // Dummy update

        return $@"
            MERGE {destinationTable} AS target
            USING {sourceTable} AS source
            ON {keyConditions}
            WHEN MATCHED THEN
                UPDATE SET {updateSet}
            WHEN NOT MATCHED THEN
                INSERT ({insertColumns})
                VALUES ({insertValues});";
    }

    private async Task<List<string>> GetPrimaryKeyColumns(DbConnection connection, string tableName, DatabaseEngineType engineType)
    {
        var (schema, table) = ParseSchemaAndTableName(tableName);

        var query = engineType switch
        {
            DatabaseEngineType.PostgreSQL => schema != null
                ? @"SELECT a.attname
                    FROM pg_index i
                    JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                    WHERE i.indrelid = @tableName::regclass AND i.indisprimary
                    ORDER BY array_position(i.indkey, a.attnum)"
                : @"SELECT a.attname
                    FROM pg_index i
                    JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                    WHERE i.indrelid = @tableName::regclass AND i.indisprimary
                    ORDER BY array_position(i.indkey, a.attnum)",

            DatabaseEngineType.MySQL => schema != null
                ? @"SELECT COLUMN_NAME
                    FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
                    WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND CONSTRAINT_NAME = 'PRIMARY'
                    ORDER BY ORDINAL_POSITION"
                : @"SELECT COLUMN_NAME
                    FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND CONSTRAINT_NAME = 'PRIMARY'
                    ORDER BY ORDINAL_POSITION",

            DatabaseEngineType.MSSQL => schema != null
                ? @"SELECT c.name
                    FROM sys.indexes i
                    INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                    INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                    INNER JOIN sys.tables t ON i.object_id = t.object_id
                    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                    WHERE i.is_primary_key = 1 AND s.name = @schema AND t.name = @table
                    ORDER BY ic.key_ordinal"
                : @"SELECT c.name
                    FROM sys.indexes i
                    INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                    INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                    INNER JOIN sys.tables t ON i.object_id = t.object_id
                    WHERE i.is_primary_key = 1 AND t.name = @table
                    ORDER BY ic.key_ordinal",

            _ => throw new NotSupportedException($"Database engine type '{engineType}' is not supported")
        };

        try
        {
            if (engineType == DatabaseEngineType.PostgreSQL)
            {
                // PostgreSQL uses $1 parameter, pass the full table name
                var fullTableName = schema != null ? $"{schema}.{table}" : table;
                var result = await connection.QueryAsync<string>(query, new { tableName = fullTableName });
                return result.ToList();
            }
            else
            {
                var result = await connection.QueryAsync<string>(query, new { schema, table });
                return result.ToList();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve primary key columns for table {Table}", tableName);
            return new List<string>();
        }
    }

    private static string GetFullExceptionMessage(Exception ex)
    {
        var messages = new List<string>();
        var currentException = ex;

        while (currentException != null)
        {
            messages.Add(currentException.Message);
            currentException = currentException.InnerException;
        }

        return string.Join(" --> ", messages);
    }
}

// Dynamic DbContext for bulk operations on tables not in the main model
internal class DynamicDbContext : DbContext
{
    private readonly string? _tableName;
    private readonly List<string>? _primaryKeys;

    public DynamicDbContext(DbContextOptions options, string? tableName = null, List<string>? primaryKeys = null) : base(options)
    {
        _tableName = tableName;
        _primaryKeys = primaryKeys;
    }

    // Add DbSet for ExpandoObject to allow BulkInsertOrUpdate operations
    public DbSet<ExpandoObject> DynamicEntities { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure ExpandoObject as keyless entity
        // The primary keys will be specified in BulkConfig for upsert operations
        modelBuilder.Entity<ExpandoObject>().HasNoKey().ToTable(_tableName ?? "DynamicTable");

        base.OnModelCreating(modelBuilder);
    }
}