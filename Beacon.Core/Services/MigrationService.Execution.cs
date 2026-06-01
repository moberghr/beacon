using System.Data.Common;
using System.Dynamic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

internal partial class MigrationService
{
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

        // Deeper validation (cron syntax, query parse, destination reachability) runs
        // lazily on first execution rather than at creation time so the user can save
        // drafts. ValidateBeforeExecution on the job opts into pre-flight checks.

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
                .Where(ds => ds.Id == migrationJob.DestinationDataSourceId)
                .FirstOrDefaultAsync(cancellationToken);

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
        {
            return sourceData;
        }

        // Server-side row transformation via the `@result` substitution syntax is not
        // wired up in this release. Reject explicitly so callers learn at job-validation
        // time instead of silently shipping un-transformed rows.
        throw new BeaconException(
            "Migration transformation scripts are not yet supported. Leave the transformation script empty.");
    }

    private async Task<(int rowsWritten, int rowsSkipped, int rowsFailed, List<string> errorDetails)> ExecuteDestinationOperation(
        Data.Entities.DataSource destinationDataSource,
        string destinationTable,
        List<Dictionary<string, object?>> data,
        MigrationMode mode,
        CancellationToken cancellationToken)
    {
        if (!destinationDataSource.DatabaseEngineType.HasValue)
            throw new BeaconException($"Destination data source {destinationDataSource.Id} is not a database type");

        var databaseEngineType = destinationDataSource.DatabaseEngineType.Value;

        DbConnection? connection = null;
        DbTransaction? transaction = null;

        try
        {
            // Create database connection based on engine type
            connection = CreateDatabaseConnection(destinationDataSource);
            await connection.OpenAsync(cancellationToken);

            // Validate destination table exists
            await ValidateDestinationTable(connection, destinationTable, databaseEngineType);

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
                        await ExecuteTruncate(connection, transaction, destinationTable, databaseEngineType);
                        (rowsWritten, rowsFailed, errorDetails) = await ExecuteInserts(connection, transaction, destinationTable, data, databaseEngineType);
                        break;

                    case MigrationMode.Insert:
                        (rowsWritten, rowsFailed, errorDetails) = await ExecuteInserts(connection, transaction, destinationTable, data, databaseEngineType);
                        break;

                    case MigrationMode.Upsert:
                        (rowsWritten, rowsFailed, errorDetails) = await ExecuteUpserts(connection, transaction, destinationTable, data, databaseEngineType);
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
}
