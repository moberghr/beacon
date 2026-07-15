using Beacon.Core.Worker;
using Warp.Core.Handlers;

namespace Beacon.SampleProject.Warp.Jobs;

// Background jobs backed by IJobService. Each handler is a thin adapter that forwards to the
// existing worker service method — the same methods Hangfire invoked before the Warp migration.

public sealed class RunMcpEvalJob : IJob
{
    public int RunId { get; init; }
}

public sealed class RunMcpEvalJobHandler(IJobService jobService) : IJobHandler<RunMcpEvalJob>
{
    public Task HandleAsync(RunMcpEvalJob message, CancellationToken cancellationToken)
        => jobService.RunMcpEval(message.RunId, cancellationToken);
}

public sealed class AggregateLearnedPatternsJob : IJob;

public sealed class AggregateLearnedPatternsJobHandler(IJobService jobService) : IJobHandler<AggregateLearnedPatternsJob>
{
    public Task HandleAsync(AggregateLearnedPatternsJob message, CancellationToken cancellationToken)
        => jobService.AggregateLearnedPatterns(cancellationToken);
}

public sealed class CleanupOldSignalsJob : IJob;

public sealed class CleanupOldSignalsJobHandler(IJobService jobService) : IJobHandler<CleanupOldSignalsJob>
{
    public Task HandleAsync(CleanupOldSignalsJob message, CancellationToken cancellationToken)
        => jobService.CleanupOldSignals(cancellationToken);
}

public sealed class ReindexEmbeddingsJob : IJob;

public sealed class ReindexEmbeddingsJobHandler(IJobService jobService) : IJobHandler<ReindexEmbeddingsJob>
{
    public Task HandleAsync(ReindexEmbeddingsJob message, CancellationToken cancellationToken)
        => jobService.ReindexEmbeddings(cancellationToken);
}
