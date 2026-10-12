using System.Data.Common;
using System.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
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
            // Parse query steps from JSON format, as the job's access was decided
            var querySteps = MigrationQuerySteps.Parse(migrationJob.QueryText);

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

            // Read the source through the temporary-query path, within the migration's own row budget (Beacon:Migrations:MaxRows):
            // a larger source fails the run here, before anything is written.
            var parameters = request.Parameters?.Select(kvp => new ParameterValue
            {
                Name = kvp.Key,
                Value = kvp.Value?.ToString() ?? ""
            }).ToList() ?? new List<ParameterValue>();

            var queryResult = await previewService.ExecuteTemporaryQueryPreview(queryData, parameters, _options.MaxRows, cancellationToken);

            // Nothing is written from a source read that failed or ran out of time, in any step or in the final query: a
            // timed-out read has no rows, and writing them would empty the destination in Truncate mode.
            if (queryResult == null
                || !queryResult.Success
                || queryResult.StepResults.Any(x => !x.Success)
                || queryResult.FinalResult is not { AllRecords: not null, TimedOut: false })
            {
                var reason = queryResult?.FinalResult?.TimedOut == true ? "the source query timed out" : queryResult?.ErrorMessage;

                throw new InvalidOperationException($"Source query execution failed: {reason ?? "no result"}");
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
            logger.LogError("Migration execution failed ({Failure})", FailureCodes.Describe(ex));
            throw;
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
