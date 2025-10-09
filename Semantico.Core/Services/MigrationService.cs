using System.Data.Common;
using System.Data.SqlClient;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Npgsql;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities.DataMigration;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.DataMigration;
using Semantico.Core.Models.Queries;

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
                ProjectId = request.ProjectId,
                QueryText = request.QueryText,
                DestinationProjectId = request.DestinationProjectId,
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
                .Include(m => m.Project)
                .Include(m => m.DestinationProject)
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
            .Include(m => m.Project)
            .Include(m => m.DestinationProject)
            .Include(m => m.Executions)
            .AsQueryable();

        // Apply filters
        if (request.ProjectId.HasValue)
        {
            query = query.Where(m => m.ProjectId == request.ProjectId.Value || m.DestinationProjectId == request.ProjectId.Value);
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
                m.ProjectId,
                m.Project != null ? m.Project.Name : "Unknown",
                m.DestinationProjectId,
                m.DestinationProject != null ? m.DestinationProject.Name : "Unknown",
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
                m.ProjectId,
                m.QueryText,
                m.DestinationProjectId,
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
            migrationJob.ProjectId = request.ProjectId;
            migrationJob.QueryText = request.QueryText;
            migrationJob.DestinationProjectId = request.DestinationProjectId;
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

            // Get destination project for data insertion
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var destinationProject = await context.Projects
                .FirstOrDefaultAsync(p => p.Id == migrationJob.DestinationProjectId, cancellationToken);

            if (destinationProject == null)
            {
                throw new InvalidOperationException("Destination project not found");
            }

            // Convert to concrete Dictionary type and apply transformation if specified
            var sourceDataDict = sourceData.Select(d => d.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)).ToList();
            var transformedData = ApplyTransformation(sourceDataDict, migrationJob.TransformationScript);

            // Execute destination insert based on migration mode
            var (rowsWritten, rowsSkipped, rowsFailed, errorDetails) = await ExecuteDestinationOperation(
                destinationProject,
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
                ProjectId = step.TryGetProperty("ProjectId", out var projectId) ? projectId.GetInt32() : 0,
                ProjectName = step.TryGetProperty("ProjectName", out var projectName) ? projectName.GetString() : "",
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
                    ProjectId = 0, // Will need to be resolved
                    ProjectName = "Unknown",
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
        Data.Entities.Project destinationProject,
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
            connection = CreateDatabaseConnection(destinationProject);
            await connection.OpenAsync(cancellationToken);

            // Validate destination table exists
            await ValidateDestinationTable(connection, destinationTable, destinationProject.DatabaseEngineType);

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
                        await ExecuteTruncate(connection, transaction, destinationTable, destinationProject.DatabaseEngineType);
                        (rowsWritten, rowsFailed, errorDetails) = await ExecuteInserts(connection, transaction, destinationTable, data, destinationProject.DatabaseEngineType);
                        break;

                    case MigrationMode.Insert:
                        (rowsWritten, rowsFailed, errorDetails) = await ExecuteInserts(connection, transaction, destinationTable, data, destinationProject.DatabaseEngineType);
                        break;

                    case MigrationMode.Upsert:
                        (rowsWritten, rowsFailed, errorDetails) = await ExecuteUpserts(connection, transaction, destinationTable, data, destinationProject.DatabaseEngineType);
                        break;

                    case MigrationMode.SyncDelete:
                        (rowsWritten, rowsSkipped, rowsFailed, errorDetails) = await ExecuteSyncDelete(connection, transaction, destinationTable, data, destinationProject.DatabaseEngineType);
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
            throw new InvalidOperationException($"Destination table '{destinationTable}' does not exist in database '{destinationProject.Name}'", ex);
        }
        catch (Exception ex) when (ex.Message.Contains("connect") || ex.Message.Contains("connection"))
        {
            throw new InvalidOperationException($"Unable to connect to destination database '{destinationProject.Name}': {ex.Message}", ex);
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

    private DbConnection CreateDatabaseConnection(Data.Entities.Project project)
    {
        try
        {
            return project.DatabaseEngineType switch
            {
                DatabaseEngineType.PostgreSQL => new NpgsqlConnection(project.ConnectionString),
                DatabaseEngineType.MySQL => new MySqlConnection(project.ConnectionString),
                DatabaseEngineType.MSSQL => new SqlConnection(project.ConnectionString),
                _ => throw new NotSupportedException($"Database engine type '{project.DatabaseEngineType}' is not supported")
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create database connection for project '{project.Name}': {ex.Message}", ex);
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
        var rowsWritten = 0;
        var rowsFailed = 0;
        var errorDetails = new List<string>();

        if (!data.Any())
            return (0, 0, errorDetails);

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
            catch (Exception ex)
            {
                rowsFailed++;
                var errorMsg = $"Row {rowsWritten + rowsFailed} failed: {ex.Message}";
                errorDetails.Add(errorMsg);
                logger.LogWarning("Failed to insert row into {Table}: {Error}", tableName, ex.Message);

                // Stop if too many errors
                if (rowsFailed > 100)
                {
                    errorDetails.Add("Too many errors, stopping insertion");
                    break;
                }
            }
        }

        return (rowsWritten, rowsFailed, errorDetails);
    }

    private async Task<(int rowsWritten, int rowsFailed, List<string> errorDetails)> ExecuteUpserts(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        DatabaseEngineType engineType)
    {
        // For now, use insert logic - proper upsert requires knowledge of primary keys
        // TODO: Implement proper UPSERT with conflict resolution
        return await ExecuteInserts(connection, transaction, tableName, data, engineType);
    }

    private async Task<(int rowsWritten, int rowsSkipped, int rowsFailed, List<string> errorDetails)> ExecuteSyncDelete(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        List<Dictionary<string, object?>> data,
        DatabaseEngineType engineType)
    {
        // For now, use insert logic - proper sync delete requires knowledge of primary keys
        // TODO: Implement proper sync with delete
        var (written, failed, errors) = await ExecuteInserts(connection, transaction, tableName, data, engineType);
        return (written, 0, failed, errors);
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