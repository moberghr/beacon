using System.Data.Common;
using System.Dynamic;
using System.Text.Json;
using Dapper;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Helpers.BulkHelpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

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

internal partial class MigrationService(
    IDbContextFactory<BeaconContext> contextFactory,
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
                .Where(m => m.Id == request.MigrationJobId)
                .FirstOrDefaultAsync(cancellationToken);

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
}
