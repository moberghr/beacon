using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Retention;
using Beacon.Core.Worker;
using Warp.Core.Handlers;
using Warp.Core.Concurrency;
using Warp.Core.Timeout;

namespace Beacon.SampleProject.Warp.Jobs;

// Background jobs backed by IJobService. Each handler is a thin adapter that forwards to the
// existing worker service method — the same methods Hangfire invoked before the Warp migration.

[Timeout(3600, Mode = TimeoutMode.Fail)]
public sealed class RunMcpEvalJob : IJob
{
    public int RunId { get; init; }
}

public sealed class RunMcpEvalJobHandler(IJobService jobService) : IJobHandler<RunMcpEvalJob>
{
    public Task HandleAsync(RunMcpEvalJob message, CancellationToken cancellationToken)
        => jobService.RunMcpEval(message.RunId, cancellationToken);
}

[Timeout(1800, Mode = TimeoutMode.Fail)]
[Mutex("mcp-learning-aggregate", Mode = ConcurrencyMode.Skip)]
public sealed class AggregateLearnedPatternsJob : IJob;

public sealed class AggregateLearnedPatternsJobHandler(IJobService jobService) : IJobHandler<AggregateLearnedPatternsJob>
{
    public Task HandleAsync(AggregateLearnedPatternsJob message, CancellationToken cancellationToken)
        => jobService.AggregateLearnedPatterns(cancellationToken);
}

[Timeout(900, Mode = TimeoutMode.Fail)]
[Mutex("mcp-learning-cleanup", Mode = ConcurrencyMode.Skip)]
public sealed class CleanupOldSignalsJob : IJob;

public sealed class CleanupOldSignalsJobHandler(IJobService jobService) : IJobHandler<CleanupOldSignalsJob>
{
    public Task HandleAsync(CleanupOldSignalsJob message, CancellationToken cancellationToken)
        => jobService.CleanupOldSignals(cancellationToken);
}

[Timeout(3600, Mode = TimeoutMode.Fail)]
[Mutex("mcp-embedding-reindex", Mode = ConcurrencyMode.Skip)]
public sealed class ReindexEmbeddingsJob : IJob;

public sealed class ReindexEmbeddingsJobHandler(IJobService jobService) : IJobHandler<ReindexEmbeddingsJob>
{
    public Task HandleAsync(ReindexEmbeddingsJob message, CancellationToken cancellationToken)
        => jobService.ReindexEmbeddings(cancellationToken);
}

// Deployment-level audit retention purge (Beacon:Mcp:Audit:RetentionDays); a no-op when unset.
[Timeout(1800, Mode = TimeoutMode.Fail)]
[Mutex("mcp-audit-retention", Mode = ConcurrencyMode.Skip)]
public sealed class PurgeExpiredMcpAuditLogsJob : IJob;

public sealed class PurgeExpiredMcpAuditLogsJobHandler(IMcpAuditRetentionService retentionService)
    : IJobHandler<PurgeExpiredMcpAuditLogsJob>
{
    public Task HandleAsync(PurgeExpiredMcpAuditLogsJob message, CancellationToken cancellationToken)
        => retentionService.PurgeExpiredAsync(cancellationToken);
}

// Security audit retention purge (Beacon:Audit:RetentionDays, at least a year); a no-op while unset (events kept forever).
// The purge records itself in the trail.
[Timeout(1800, Mode = TimeoutMode.Fail)]
[Mutex("security-audit-retention", Mode = ConcurrencyMode.Skip)]
public sealed class PurgeExpiredSecurityAuditEventsJob : IJob;

public sealed class PurgeExpiredSecurityAuditEventsJobHandler(ISecurityAuditRetentionService retentionService)
    : IJobHandler<PurgeExpiredSecurityAuditEventsJob>
{
    public Task HandleAsync(PurgeExpiredSecurityAuditEventsJob message, CancellationToken cancellationToken)
        => retentionService.PurgeExpiredAsync(cancellationToken);
}

// Security audit verification: hourly, the events written since the last run are checked against their HMACs; failures
// are logged at Error (9108), each run writes a checkpoint to the log stream (9109) and records what it checked.
[Timeout(1800, Mode = TimeoutMode.Fail)]
[Mutex("security-audit-verify", Mode = ConcurrencyMode.Skip)]
public sealed class VerifySecurityAuditEventsJob : IJob;

public sealed class VerifySecurityAuditEventsJobHandler(ISecurityAuditVerificationService verificationService)
    : IJobHandler<VerifySecurityAuditEventsJob>
{
    public Task HandleAsync(VerifySecurityAuditEventsJob message, CancellationToken cancellationToken)
        => verificationService.VerifyNewEventsAsync(cancellationToken);
}
