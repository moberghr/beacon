namespace Semantico.Core.Services;

public interface IMcpLearningAggregationService
{
    Task AggregateLearnedPatternsAsync(CancellationToken ct = default);
    Task CleanupOldSignalsAsync(int retentionDays = 90, CancellationToken ct = default);
}
