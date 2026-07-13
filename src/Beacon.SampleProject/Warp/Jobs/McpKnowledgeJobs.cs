using Beacon.Core.Worker;
using Warp.Core.Handlers;

namespace Beacon.SampleProject.Warp.Jobs;

// Tier-3 knowledge-base background jobs (⑨ chunking + top-K, ⑩ contextual retrieval). The handler forwards
// through the IJobService facade (like every sibling maintenance job in McpMaintenanceJobs) — a re-index
// chunks each project's latest documentation, optionally prepends an LLM-generated contextual blurb, embeds
// the chunks, and upserts them into McpDocChunk + McpEmbedding.

public sealed class ReindexDocChunksJob : IJob;

public sealed class ReindexDocChunksJobHandler(IJobService jobService) : IJobHandler<ReindexDocChunksJob>
{
    public Task HandleAsync(ReindexDocChunksJob message, CancellationToken ct) => jobService.ReindexDocChunks(ct);
}
