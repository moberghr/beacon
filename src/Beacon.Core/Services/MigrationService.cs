using System.Data;
using System.Dynamic;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;
using Beacon.Core.DataMigration;
using Beacon.Core.HostData;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Services;

public interface IMigrationService
{
    Task<CreateMigrationJobResponse> CreateMigrationJob(CreateMigrationJobRequest request, CancellationToken cancellationToken);
    Task<ExecuteMigrationJobResponse> ExecuteMigrationJob(ExecuteMigrationJobRequest request, CancellationToken cancellationToken);
    Task<PagedList<MigrationExecutionDto>> GetMigrationExecutions(GetMigrationExecutionsRequest request, CancellationToken cancellationToken);
    Task<BaseResponse> DeleteMigrationJob(int id, CancellationToken cancellationToken, bool forceDelete = false);
}

/// <summary>
/// Migration jobs and their runs. A job runs once at a time (a run of a job with a run in progress is refused, 409) and is
/// stopped after its timeout (<see cref="MigrationJobSettings"/>). A run reads its steps in-process, storing no query (the execution gate and the
/// read-only provider path), within its own row budget (<see cref="MigrationOptions.MaxRows"/>), and writes only to a
/// migration write target (<see cref="MigrationWriteTargets"/>), connecting with that data source's credentials — the only
/// place Beacon connects with them. Nothing is created or run while the deployment does not run migrations
/// (<see cref="MigrationOptions.Enabled"/>). While the change approval workflow covers migration jobs
/// (<see cref="ApprovalWorkflowOptions.CoversMigrationJobs"/>), a job created is submitted for approval (the caller is its
/// requester) and a job runs only while its definition is the one a second person approved
/// (<see cref="MigrationJob.ApprovedDefinitionHash"/>); a refused run is recorded as failed.
/// </summary>
internal partial class MigrationService(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceConnectionResolver connectionResolver,
    IQueryService queryService,
    IQueryExecutionPreviewService previewService,
    IBeaconUserContext userContext,
    DataMigration.MigrationBulkWriters writers,
    ILogger<MigrationService> logger,
    IOptions<MigrationOptions> migrationOptions,
    ChangeApprovalSubmitter changeApprovals,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider) : IMigrationService
{
    /// <summary>Why a run of a job whose definition is not approved is refused (the failed run's message).</summary>
    internal const string NotApprovedMessage =
        "Not run: the job's definition waits for approval. A second person approves it on the approvals page; until then the job does not run.";

    /// <summary>Why a run of a job that already has a run starting or in progress does not start (409 on the route).</summary>
    internal const string RunInProgressMessage =
        "Not run: another run of this job is starting or in progress. Wait for it to end, then run the job again.";

    /// <summary>What a Running row left by a host that stopped during the run is closed with.</summary>
    internal const string AbandonedRunMessage =
        "Abandoned: the host stopped during the run, which never ended. Closed when the job was next run.";

    /// <summary>The security event reason of a run refused because another run of the job is starting or in progress.</summary>
    internal const string RunInProgressReason = "run_in_progress";

    /// <summary>The security event reason of a run closed as abandoned.</summary>
    internal const string AbandonedRunReason = "abandoned";

    /// <summary>Why a run whose destination lock another run of the job holds wrote nothing.</summary>
    internal const string DestinationBusyMessage =
        "Nothing was written: another run of this job holds its destination's migration lock. Wait for it to end, then run the job again.";

    internal const string DestinationBusyReason = "destination_busy";

    /// <summary>What a run whose destination commit failed records: whether its rows were written is not known.</summary>
    internal const string CommitUnknownMessage =
        "The destination's commit could not be confirmed: check the destination table before running this job again.";

    internal const string CommitUnknownReason = "commit_unknown";

    private readonly MigrationOptions _options = migrationOptions.Value;

    public async Task<CreateMigrationJobResponse> CreateMigrationJob(CreateMigrationJobRequest request, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new CreateMigrationJobResponse(0, false, MigrationWriteTargets.DisabledMessage);
        }

        // Before anything is validated or written: under the workflow the caller is the job's requester, and a caller
        // Beacon cannot name creates nothing (403).
        var requester = await changeApprovals.RequesterAsync(ChangeApprovalKind.MigrationJob, null, cancellationToken);
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        try
        {
            // Validate request
            var validationResult = await ValidateMigrationJobRequest(request, cancellationToken);
            if (!validationResult.IsValid)
            {
                return new CreateMigrationJobResponse(0, false, "Validation failed", validationResult);
            }

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            MigrationJob NewJob() => new()
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
                ChangedOn = DateTime.UtcNow,
                ApprovedDefinitionHash = requester == null ? null : ChangeApprovalHashes.NothingApproved,
                CreatedByUserId = actor.UserId
            };

            var migrationJob = requester == null
                ? await CreateAsync(context, NewJob, cancellationToken)
                : await CreateForApprovalAsync(context, requester, NewJob, cancellationToken);

            logger.LogInformation("Created migration job {Name} with ID {Id}", request.Name, migrationJob.Id);

            return new CreateMigrationJobResponse(migrationJob.Id, true, null, validationResult) { AwaitingApproval = requester != null };
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

            // Under the workflow a job runs only the definition a second person approved, whoever runs it and however (a
            // manual run, a schedule): a refused run never starts, and is recorded as a failed run with the reason.
            if (changeApprovals.Covers(ChangeApprovalKind.MigrationJob)
                && !ChangeApprovalHashes.Allows(migrationJob.ApprovedDefinitionHash, MigrationJobDefinition.Of(migrationJob).Hash))
            {
                logger.LogWarning("Migration job {MigrationJobId} was not run: its definition is not approved", migrationJob.Id);

                return await RecordRefusedRunAsync(context, migrationJob, request, NotApprovedMessage, "not_approved", cancellationToken);
            }

            // Writes only into a write target, from sources that are not one; a refused run never starts, and is recorded as a
            // failed run so the job's history shows why it did not copy anything.
            var targetRefusal = await TargetRefusalAsync(context, migrationJob, cancellationToken);
            if (targetRefusal != null)
            {
                logger.LogWarning("Migration job {MigrationJobId} was not run: its destination is not a migration write target, or a source it reads is one", migrationJob.Id);

                return await RecordRefusedRunAsync(context, migrationJob, request, targetRefusal, "write_target_refused", cancellationToken);
            }

            var modeRefusal = MigrationJobSettings.RunRefusal(migrationJob.Mode);
            if (modeRefusal != null)
            {
                logger.LogWarning("Migration job {MigrationJobId} was not run: its stored mode is not one of the three", migrationJob.Id);

                return await RecordRefusedRunAsync(context, migrationJob, request, modeRefusal, "unsupported_mode", cancellationToken);
            }

            // One run of a job at a time, on every host: a run of a job with a run starting or in progress does not start. It
            // reads and writes nothing and adds no history row; the response says so (RunInProgress) and the route answers 409.
            var execution = await StartRunAsync(context, migrationJob, request, cancellationToken);
            if (execution == null)
            {
                logger.LogInformation("Migration job {MigrationJobId} was not run: another run of it is starting or in progress", migrationJob.Id);
                await securityAudit.RecordAsync(RunInProgressRefused(migrationJob), CancellationToken.None);

                return new ExecuteMigrationJobResponse(0, MigrationStatus.Failed, ErrorMessage: RunInProgressMessage) { RunInProgress = true };
            }

            // From here the run's row exists: it is closed whatever happens (a failure, the timeout, a cancelled request) and
            // its id is the answer, so the job is never left blocked by a run that is no longer going on.
            var timeout = MigrationJobSettings.RunTimeout(migrationJob.TimeoutMinutes);
            using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
            using var runSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            var progress = new MigrationRunProgress();
            string? failureCode = null;
            List<string>? warnings = null;

            try
            {
                var migrationResult = await ExecuteMigrationInternal(migrationJob, execution, request, progress, runSource.Token);

                execution.Status = migrationResult.Status;
                execution.SourceRowsRead = migrationResult.SourceRowsRead;
                execution.DestinationRowsWritten = migrationResult.DestinationRowsWritten;
                execution.RowsSkipped = migrationResult.RowsSkipped;
                execution.RowsFailed = migrationResult.RowsFailed;
                execution.ErrorMessage = CapErrorMessage(migrationResult.ErrorMessage);
                warnings = migrationResult.Warnings;
            }
            catch (Exception ex)
            {
                // A run stopped by its timeout says so; once the write's commit has started, what the commit did is the outcome.
                var timedOut = !progress.CommitStarted
                    && timeoutSource.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested;
                var reported = ReportedFailure(ex);
                failureCode = reported?.ReasonCode ?? (timedOut ? "timed_out" : null);
                execution.Status = MigrationStatus.Failed;
                execution.ErrorMessage = CapErrorMessage(reported?.Message ?? (timedOut ? TimedOutMessage(timeout) : GetFullExceptionMessage(ex)));

                logger.LogError("Failed migration job {Name} execution {ExecutionId} ({Failure}, timed out: {TimedOut})", migrationJob.Name, execution.Id, FailureCodes.Describe(ex), timedOut);
            }

            execution.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            await CloseRunAsync(context, migrationJob, execution, failureCode);

            logger.LogInformation("Migration job {Name} execution {ExecutionId} ended with status {Status}",
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
                warnings ?? new List<string>()
            );
        }
        catch (Exception ex)
        {
            logger.LogError("Error executing migration job {MigrationJobId} ({Failure})", request.MigrationJobId, FailureCodes.Describe(ex));
            return new ExecuteMigrationJobResponse(0, MigrationStatus.Failed, ErrorMessage: GetFullExceptionMessage(ex));
        }
    }

    public async Task<PagedList<MigrationExecutionDto>> GetMigrationExecutions(GetMigrationExecutionsRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // A run is a record of what was written: it stays listed after its job is archived.
        var query = context.MigrationExecutions
            .IgnoreQueryFilters()
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
            )
            {
                JobArchived = e.MigrationJob.ArchivedTime != null
            })
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
                migrationJob.RecordChangeBy(await actorAccessor.GetCurrentAsync(cancellationToken), timeProvider);
            }

            await securityAudit.StageAsync(
                context,
                new SecurityAuditEntry(SecurityAuditActions.MigrationJobDeleted)
                    .On(SecurityAuditResources.MigrationJob, id)
                    .With(new SecurityAuditDetails().Add("forceDelete", forceDelete)),
                cancellationToken);
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

    private async Task<MigrationJob> CreateAsync(BeaconContext context, Func<MigrationJob> newJob, CancellationToken cancellationToken)
    {
        var migrationJob = newJob();
        context.MigrationJobs.Add(migrationJob);
        await securityAudit.SaveWithEventsAsync(context, () => [JobCreated(migrationJob, null)], cancellationToken);

        return migrationJob;
    }

    // The job and its request in one unit of work: the job's save gives it the id its request names, and both commit
    // together. Through the execution strategy, so a host that enables retry-on-failure gets a retried unit; a retry starts
    // from a clean tracker and a new job.
    private async Task<MigrationJob> CreateForApprovalAsync(
        BeaconContext context,
        ChangeRequester requester,
        Func<MigrationJob> newJob,
        CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var migrationJob = newJob();
            context.MigrationJobs.Add(migrationJob);
            await context.SaveChangesAsync(cancellationToken);

            var definition = MigrationJobDefinition.Of(migrationJob);
            var proposal = new ChangeProposal(
                ChangeApprovalKind.MigrationJob,
                migrationJob.Id,
                migrationJob.Name,
                PreviousDefinitionHash: null,
                definition.Hash,
                NeedsApproval: true,
                definition.View(),
                Previous: null,
                "Migration job created");
            var submission = await changeApprovals.SubmitAsync(context, requester, proposal, cancellationToken);
            await securityAudit.SaveWithEventsAsync(
                context,
                () => new[] { JobCreated(migrationJob, submission) }.Concat(ChangeApprovalAuditEntries.Submitted(submission)),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return migrationJob;
        });
    }

    // The job's ids, mode and whether it is enabled, and the change request it waits on: never its query or destination table.
    private static SecurityAuditEntry JobCreated(MigrationJob migrationJob, ChangeSubmission? submission)
    {
        return new SecurityAuditEntry(SecurityAuditActions.MigrationJobCreated)
            .On(SecurityAuditResources.MigrationJob, migrationJob.Id)
            .With(new SecurityAuditDetails()
                .Add("sourceDataSourceId", migrationJob.DataSourceId)
                .Add("destinationDataSourceId", migrationJob.DestinationDataSourceId)
                .Add("mode", migrationJob.Mode)
                .Add("isEnabled", migrationJob.IsEnabled)
                .Add("changeApprovalId", submission?.Request?.Id));
    }

    // A run of the job begins: by whom it was started is the event's actor; manual or scheduled.
    private static SecurityAuditEntry JobStarted(MigrationJob migrationJob, int executionId, ExecuteMigrationJobRequest request)
    {
        return new SecurityAuditEntry(SecurityAuditActions.MigrationJobStarted)
            .On(SecurityAuditResources.MigrationJob, migrationJob.Id)
            .With(new SecurityAuditDetails()
                .Add("executionId", executionId)
                .Add("manual", request.IsManualExecution)
                .Add("sourceDataSourceId", migrationJob.DataSourceId)
                .Add("destinationDataSourceId", migrationJob.DestinationDataSourceId));
    }

    // How a run ended: completed, failed (with its counts, and a reason code when it timed out or was abandoned) or refused
    // before it started (with a reason code).
    private static SecurityAuditEntry JobRunEnded(
        MigrationJob migrationJob,
        MigrationExecutionHistory execution,
        string? refusalCode,
        string? failureCode = null)
    {
        var outcome = refusalCode != null
            ? SecurityAuditOutcomes.Refused
            : execution.Status == MigrationStatus.Completed
                ? SecurityAuditOutcomes.Success
                : SecurityAuditOutcomes.Failure;

        return new SecurityAuditEntry(SecurityAuditActions.MigrationJobRunEnded)
            .On(SecurityAuditResources.MigrationJob, migrationJob.Id)
            .Ending(outcome)
            .With(new SecurityAuditDetails()
                .Add("executionId", execution.Id)
                .Add("status", execution.Status)
                .Add("sourceRowsRead", execution.SourceRowsRead)
                .Add("destinationRowsWritten", execution.DestinationRowsWritten)
                .Add("rowsFailed", execution.RowsFailed)
                .AddCode("reason", refusalCode ?? failureCode));
    }

    // A run refused because the job has a run starting or in progress: no run row exists, so the event names none.
    private static SecurityAuditEntry RunInProgressRefused(MigrationJob migrationJob)
    {
        return new SecurityAuditEntry(SecurityAuditActions.MigrationJobRunEnded)
            .On(SecurityAuditResources.MigrationJob, migrationJob.Id)
            .Ending(SecurityAuditOutcomes.Refused)
            .With(new SecurityAuditDetails()
                .Add("sourceDataSourceId", migrationJob.DataSourceId)
                .Add("destinationDataSourceId", migrationJob.DestinationDataSourceId)
                .AddCode("reason", RunInProgressReason));
    }

    // The run's history row, saved as Running only while the job has no run in progress; null when it has one. The check and
    // the save share one serializable transaction, so two hosts that start the same job at once cannot both pass it: the
    // database refuses one of them, whose claim is tried once more (and then finds the other's run). A Running row older
    // than a run can last (the job's timeout and a margin) was left by a host that stopped during the run: it is closed
    // as abandoned in the same transaction and blocks nothing.
    private async Task<MigrationExecutionHistory?> StartRunAsync(
        BeaconContext context,
        MigrationJob migrationJob,
        ExecuteMigrationJobRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ClaimRunAsync(context, migrationJob, request, cancellationToken);
        }
        catch (Exception ex) when (DbSerializationConflict.IsSerializationConflict(ex))
        {
            logger.LogInformation("Migration job {MigrationJobId}: a concurrent start conflicted with this run's; trying once more", migrationJob.Id);
        }

        try
        {
            return await ClaimRunAsync(context, migrationJob, request, cancellationToken);
        }
        catch (Exception ex) when (DbSerializationConflict.IsSerializationConflict(ex))
        {
            return null;
        }
    }

    // One claim, through the execution strategy (a host that enables retry-on-failure gets a retried unit, which starts from
    // a clean tracker and a new row). Once the row is saved the commit is not cancelled: a claim committed for a request
    // that is gone would leave a Running row nobody closes.
    private async Task<MigrationExecutionHistory?> ClaimRunAsync(
        BeaconContext context,
        MigrationJob migrationJob,
        ExecuteMigrationJobRequest request,
        CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var startedAt = timeProvider.GetUtcNow().UtcDateTime;
            var inProgressSince = startedAt - MigrationJobSettings.RunInProgressWindow(migrationJob.TimeoutMinutes);
            var running = await context.MigrationExecutions
                .Where(x => x.MigrationJobId == migrationJob.Id)
                .Where(x => x.Status == MigrationStatus.Running)
                .ToListAsync(cancellationToken);
            if (running.Any(x => x.StartedAt > inProgressSince))
            {
                return null;
            }

            foreach (var abandoned in running)
            {
                abandoned.Status = MigrationStatus.Failed;
                abandoned.CompletedAt = startedAt;
                abandoned.ErrorMessage = AbandonedRunMessage;
                await securityAudit.StageAsync(context, JobRunEnded(migrationJob, abandoned, null, AbandonedRunReason), CancellationToken.None);
            }

            var execution = new MigrationExecutionHistory
            {
                MigrationJobId = migrationJob.Id,
                StartedAt = startedAt,
                Status = MigrationStatus.Running,
                ExecutedQuery = migrationJob.QueryText,
                QueryParameters = request.Parameters != null ? JsonSerializer.Serialize(request.Parameters) : null
            };

            // The run is on record before it touches a data source: started (by the caller, or the system for a scheduled
            // run) in the save that creates its history row, and ended in the save that completes it.
            context.MigrationExecutions.Add(execution);
            await securityAudit.SaveWithEventsAsync(context, () => [JobStarted(migrationJob, execution.Id, request)], CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);

            return execution;
        });
    }

    // The run's row closed with what happened, and its end event, in one save. When that save fails the row is closed from a
    // fresh context, only while it is still Running, and the event recorded on its own, so a run that ended never stays
    // Running. Never throws: the run's outcome is the answer.
    private async Task CloseRunAsync(BeaconContext context, MigrationJob migrationJob, MigrationExecutionHistory execution, string? failureCode)
    {
        try
        {
            await securityAudit.StageAsync(context, JobRunEnded(migrationJob, execution, null, failureCode), CancellationToken.None);
            await context.SaveChangesAsync(CancellationToken.None);

            return;
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Could not record the end of migration job {MigrationJobId} execution {ExecutionId} ({Failure}); closing its row again",
                migrationJob.Id,
                execution.Id,
                FailureCodes.Describe(ex));
        }

        try
        {
            await using var fresh = await contextFactory.CreateDbContextAsync(CancellationToken.None);
            var status = execution.Status;
            var completedAt = execution.CompletedAt;
            var sourceRowsRead = execution.SourceRowsRead;
            var destinationRowsWritten = execution.DestinationRowsWritten;
            var rowsSkipped = execution.RowsSkipped;
            var rowsFailed = execution.RowsFailed;
            var errorMessage = CapErrorMessage(execution.ErrorMessage);
            await fresh.MigrationExecutions
                .Where(x => x.Id == execution.Id)
                .Where(x => x.Status == MigrationStatus.Running)
                .ExecuteUpdateAsync(x => x
                    .SetProperty(y => y.Status, status)
                    .SetProperty(y => y.CompletedAt, completedAt)
                    .SetProperty(y => y.SourceRowsRead, sourceRowsRead)
                    .SetProperty(y => y.DestinationRowsWritten, destinationRowsWritten)
                    .SetProperty(y => y.RowsSkipped, rowsSkipped)
                    .SetProperty(y => y.RowsFailed, rowsFailed)
                    .SetProperty(y => y.ErrorMessage, errorMessage), CancellationToken.None);
            await securityAudit.RecordAsync(JobRunEnded(migrationJob, execution, null, failureCode), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Could not close migration job {MigrationJobId} execution {ExecutionId} ({Failure}); it counts as in progress until the job's timeout and margin pass",
                migrationJob.Id,
                execution.Id,
                FailureCodes.Describe(ex));
        }
    }

    // Whether the destination write's commit has started: from then on, what the commit did is the run's outcome.
    private sealed class MigrationRunProgress
    {
        public bool CommitStarted { get; set; }
    }

    // The failure a run reports as itself (its own message and reason code), wherever it sits in the exception chain.
    private static MigrationRunFailedException? ReportedFailure(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is MigrationRunFailedException reported)
            {
                return reported;
            }
        }

        return null;
    }

    // The error column holds 4,000 characters: a longer text would fail the save and leave a committed run recorded as failed
    // or still running.
    internal const int ErrorMessageMaxLength = 4000;

    internal static string? CapErrorMessage(string? message) =>
        message is { Length: > ErrorMessageMaxLength } ? message[..ErrorMessageMaxLength] : message;

    private static string TimedOutMessage(TimeSpan timeout) =>
        $"The run timed out after {timeout.TotalMinutes:0} minutes (the job's timeout) and was stopped.";

    // A run refused before it started, recorded as a failed run so the job's history shows why it copied nothing.
    private async Task<ExecuteMigrationJobResponse> RecordRefusedRunAsync(
        BeaconContext context,
        MigrationJob migrationJob,
        ExecuteMigrationJobRequest request,
        string reason,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var refusedAt = DateTime.UtcNow;
        var refused = new MigrationExecutionHistory
        {
            MigrationJobId = migrationJob.Id,
            StartedAt = refusedAt,
            CompletedAt = refusedAt,
            Status = MigrationStatus.Failed,
            ExecutedQuery = migrationJob.QueryText,
            QueryParameters = request.Parameters != null ? JsonSerializer.Serialize(request.Parameters) : null,
            ErrorMessage = CapErrorMessage(reason)
        };
        context.MigrationExecutions.Add(refused);
        await securityAudit.SaveWithEventsAsync(context, () => [JobRunEnded(migrationJob, refused, reasonCode)], cancellationToken);

        return new ExecuteMigrationJobResponse(refused.Id, MigrationStatus.Failed, ErrorMessage: reason);
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
