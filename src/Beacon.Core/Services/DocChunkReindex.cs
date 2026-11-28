using Microsoft.Extensions.Logging;
using Beacon.Core.Helpers;

namespace Beacon.Core.Services;

/// <summary>
/// Indexes a project's glossary terms and documentation chunks right after a write that changed them committed (a glossary
/// term created or edited, a documentation generation finished), so retrieval does not wait for the recurring re-index.
/// Best effort: a failure is logged as a Warning with the project id and failure code only (never the exception or any
/// content) and the recurring re-index catches up; the write is never failed or rolled back by it. A cancellation by the
/// caller (the write already committed) is logged as Information. A host without
/// Beacon.AI registers no indexer, and nothing is indexed.
/// </summary>
public static class DocChunkReindex
{
    public static async Task AfterWriteAsync(
        IDocChunkIndexingService? indexing,
        int projectId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (indexing == null)
        {
            return;
        }

        try
        {
            await indexing.ReindexProjectAsync(projectId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away after its write committed: nothing failed, the recurring re-index catches up.
            logger.LogInformation(
                "Indexing the glossary and documentation chunks of project {ProjectId} after a write was cancelled; the recurring re-index catches up.",
                projectId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Indexing the glossary and documentation chunks of project {ProjectId} after a write failed; the recurring re-index catches up ({Failure}).",
                projectId,
                FailureCodes.Describe(ex));
        }
    }
}
