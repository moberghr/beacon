using System.Reflection;
using Beacon.SampleProject.Warp;
using Beacon.SampleProject.Warp.Jobs;
using FluentAssertions;
using NUnit.Framework;
using Warp.Core.Concurrency;
using Warp.Core.Handlers;


namespace Beacon.Tests.Unit.Warp;

[TestFixture]
public class WarpJobBoundsTests
{
    // Every Beacon job with its timeout in seconds, its mutex key (null: overlap is allowed) and why.
    private static readonly (Type Job, int TimeoutSeconds, string? MutexKey, string Reason)[] Expected =
    [
        (typeof(ReEncryptStoredValuesJob), 3600, "reencrypt-stored-values", "rewrites every stored value; two runs would race on the same rows"),
        (typeof(EncryptRecipientSecretsJob), 3600, "encrypt-recipient-secrets", "rewrites stored recipient secrets; one run at a time"),
        (typeof(AggregateLearnedPatternsJob), 1800, "mcp-learning-aggregate", "recurring; a slow run must not overlap the next"),
        (typeof(CleanupOldSignalsJob), 900, "mcp-learning-cleanup", "recurring purge"),
        (typeof(ReindexEmbeddingsJob), 3600, "mcp-embedding-reindex", "recurring full re-embed"),
        (typeof(ReindexDocChunksJob), 3600, "mcp-docchunk-reindex", "recurring full re-chunk and re-embed"),
        (typeof(PurgeExpiredMcpAuditLogsJob), 1800, "mcp-audit-retention", "recurring purge"),
        (typeof(PurgeExpiredSecurityAuditEventsJob), 1800, "security-audit-retention", "recurring purge"),
        (typeof(VerifySecurityAuditEventsJob), 1800, "security-audit-verify", "recurring hash verification reads a watermark"),
        (typeof(RunMcpEvalJob), 3600, null, "one run per request id; runs are independent"),
        (typeof(GenerateProjectDocumentationJob), 3600, null, "per project and user; independent"),
        (typeof(AiActorThinkCycleJob), 900, null, "per actor subscription; independent"),
        (typeof(ExecuteSubscriptionQueryJob), 1800, null, "per subscription; the run guard prevents duplicates"),
        (typeof(EvaluateDataContractJob), 1800, null, "per contract; independent"),
        (typeof(RefreshDataSourceMetadataJob), 3600, null, "per data source; mutex keys are static so none is declared"),
        (typeof(DataRetentionJob), 3600, "data-retention", "recurring purge across every store; one run at a time"),
        (typeof(RunBackgroundTaskJob), 90000, null, "per task; the task row's claim prevents duplicates, ceiling above a 24 h migration run")
    ];

    [Test]
    public void EveryJobDeclaresItsTimeoutAndMutex()
    {
        foreach (var (job, timeoutSeconds, mutexKey, reason) in Expected)
        {
            var timeout = job.GetCustomAttribute<global::Warp.Core.Timeout.TimeoutAttribute>();
            var mutex = job.GetCustomAttribute<MutexAttribute>();

            timeout.Should().NotBeNull($"{job.Name} needs a timeout ({reason})");
            timeout!.Seconds.Should().Be(timeoutSeconds, job.Name);
            timeout.Mode.Should().Be(global::Warp.Core.Timeout.TimeoutMode.Fail, $"{job.Name}: a timed-out job stays failed in Warp");
            mutex?.Key.Should().Be(mutexKey, job.Name);
            (mutex == null).Should().Be(mutexKey == null, $"{job.Name}: {reason}");
        }
    }

    [Test]
    public void EveryJobTypeInTheHostIsListed()
    {
        var jobs = typeof(ReEncryptStoredValuesJob).Assembly
            .GetTypes()
            .Where(x => typeof(IJob).IsAssignableFrom(x))
            .Where(x => x is { IsClass: true, IsAbstract: false })
            .ToList();

        jobs.Should().BeEquivalentTo(Expected.Select(x => x.Job));
    }

    [Test]
    public void BootCancellationCancelsAfterItsCeiling()
    {
        using var boot = new BootCancellation(TimeSpan.FromMilliseconds(50));

        boot.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)).Should().BeTrue();
        boot.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Test]
    public void BootCancellationStaysOpenBeforeItsCeiling()
    {
        using var boot = new BootCancellation(TimeSpan.FromMinutes(10));

        boot.Token.IsCancellationRequested.Should().BeFalse();
    }
}
