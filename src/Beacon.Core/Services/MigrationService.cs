using System.Data.Common;
using System.Dynamic;
using System.Text.Json;
using Dapper;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
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
    Task<PagedList<MigrationExecutionDto>> GetMigrationExecutions(GetMigrationExecutionsRequest request, CancellationToken cancellationToken);
    Task<BaseResponse> DeleteMigrationJob(int id, CancellationToken cancellationToken, bool forceDelete = false);
}

/// <summary>
/// Migration jobs and their runs. A run reads its source through the temporary-query path (the execution gate and the
/// read-only provider path), within its own row budget (<see cref="MigrationOptions.MaxRows"/>), and writes only to a
/// migration write target (<see cref="MigrationWriteTargets"/>), connecting with that data source's credentials — the only
/// place Beacon connects with them. Nothing is created or run while the deployment does not run migrations
/// (<see cref="MigrationOptions.Enabled"/>).
/// </summary>
internal partial class MigrationService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    IQueryService queryService,
    IQueryExecutionPreviewService previewService,
    IBeaconUserContext userContext,
    ILogger<MigrationService> logger,
    IOptions<MigrationOptions> migrationOptions) : IMigrationService
{
    private readonly MigrationOptions _options = migrationOptions.Value;

    public async Task<CreateMigrationJobResponse> CreateMigrationJob(CreateMigrationJobRequest request, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new CreateMigrationJobResponse(0, false, MigrationWriteTargets.DisabledMessage);
        }

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
                ChangedBy = userContext.UserId ?? userContext.UserName ?? "system",
                ChangedOn = DateTime.UtcNow
            };

            context.MigrationJobs.Add(migrationJob);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Created migration job {Name} with ID {Id}", request.Name, migrationJob.Id);

            return new CreateMigrationJobResponse(migrationJob.Id, true, null, validationResult);
        }
        catch (Exception ex)
        {
            logger.LogError("Error creating migration job {Name} ({Failure})", request.Name, FailureCodes.Describe(ex));
            return new CreateMigrationJobResponse(0, false, ex.Message);
        }
    }

    public async Task<ExecuteMigrationJobResponse> ExecuteMigrationJob(ExecuteMigrationJobRequest request, CancellationToken cancellationToken)
    {
        // The deployment lock holds for every caller, before anything is read or recorded: a run refused by it, or of a job
        // that does not exist or is disabled, is answered to the caller only, with no history row.
        if (!_options.Enabled)
        {
            return new ExecuteMigrationJobResponse(0, MigrationStatus.Failed, ErrorMessage: MigrationWriteTargets.DisabledMessage);
        }

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

            // Writes only into a write target, from sources that are not one; a refused run never starts, and is recorded as a
            // failed run so the job's history shows why it did not copy anything.
            var targetRefusal = await TargetRefusalAsync(context, migrationJob, cancellationToken);
            if (targetRefusal != null)
            {
                logger.LogWarning("Migration job {MigrationJobId} was not run: its destination is not a migration write target, or a source it reads is one", migrationJob.Id);
                var refusedAt = DateTime.UtcNow;
                var refused = new MigrationExecutionHistory
                {
                    MigrationJobId = migrationJob.Id,
                    StartedAt = refusedAt,
                    CompletedAt = refusedAt,
                    Status = MigrationStatus.Failed,
                    ExecutedQuery = migrationJob.QueryText,
                    QueryParameters = request.Parameters != null ? System.Text.Json.JsonSerializer.Serialize(request.Parameters) : null,
                    ErrorMessage = targetRefusal
                };
                context.MigrationExecutions.Add(refused);
                await context.SaveChangesAsync(cancellationToken);

                return new ExecuteMigrationJobResponse(refused.Id, MigrationStatus.Failed, ErrorMessage: targetRefusal);
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

                logger.LogError("Failed migration job {Name} execution {ExecutionId} ({Failure})", migrationJob.Name, execution.Id, FailureCodes.Describe(ex));

                return new ExecuteMigrationJobResponse(execution.Id, MigrationStatus.Failed, ErrorMessage: GetFullExceptionMessage(ex));
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Error executing migration job {MigrationJobId} ({Failure})", request.MigrationJobId, FailureCodes.Describe(ex));
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

    public async Task<PagedList<MigrationExecutionDto>> GetMigrationExecutions(GetMigrationExecutionsRequest request, CancellationToken cancellationToken)
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

        if (request.Readable != null)
        {
            query = query
                .WhereSourceAccessible(context, request.Readable, x => x.MigrationJob.DataSourceId)
                .WhereSourceAccessible(context, request.Readable, x => x.MigrationJob.DestinationDataSourceId);
        }

        // MigrationExecutionDto is positional, so EF cannot order through it: sort the entity, then project.
        var criterion = request.SortCriteria().FirstOrDefault();
        var descending = criterion == null || criterion.SortDirection == SortDirection.Descending;
        IOrderedQueryable<MigrationExecutionHistory> ordered = criterion?.SortColumn.ToLowerInvariant() switch
        {
            "completedat" => descending ? query.OrderByDescending(e => e.CompletedAt) : query.OrderBy(e => e.CompletedAt),
            "status" => descending ? query.OrderByDescending(e => e.Status) : query.OrderBy(e => e.Status),
            "sourcerowsread" => descending ? query.OrderByDescending(e => e.SourceRowsRead) : query.OrderBy(e => e.SourceRowsRead),
            _ => descending ? query.OrderByDescending(e => e.StartedAt) : query.OrderBy(e => e.StartedAt)
        };

        return await ordered
            .ThenBy(e => e.Id)
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
            .ToPagedListPreservingOrderAsync(request, cancellationToken);
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
            logger.LogError("Error deleting migration job {Id} ({Failure})", id, FailureCodes.Describe(ex));
            return new BaseResponse { Success = false, Message = ex.Message };
        }
    }

    // Why the job may not run as stored: its destination is not a write target (or no longer exists), or a source it
    // reads is one. Null when it may.
    private static async Task<string?> TargetRefusalAsync(BeaconContext context, MigrationJob migrationJob, CancellationToken cancellationToken)
    {
        if (migrationJob.DestinationDataSource == null)
        {
            return "Destination data source not found.";
        }

        var refusal = MigrationWriteTargets.DestinationRefusal(migrationJob.DestinationDataSource);
        if (refusal != null)
        {
            return refusal;
        }

        var sourceIds = MigrationQuerySteps.SourceIds(migrationJob.DataSourceId, migrationJob.QueryText);
        var readsWriteTarget = await context.DataSources
            .Where(x => sourceIds.Contains(x.Id))
            .Where(x => x.AllowMigrationWrites)
            .AnyAsync(cancellationToken);

        return readsWriteTarget ? MigrationWriteTargets.ReadRefusedMessage : null;
    }
}
