using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services;

namespace Beacon.MCP.Services;

internal sealed class McpSignalService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    ILogger<McpSignalService> logger)
{
    // Returns the persisted signal's Id (populated post-save) so callers can surface it for human feedback,
    // or null when learning is disabled or recording failed. Recording stays non-optional and best-effort:
    // a transient failure is swallowed + logged, never propagated to the tool call.
    public async Task<int?> RecordSignalAsync(McpQuerySignal signal, CancellationToken ct = default)
    {
        try
        {
            var settings = await settingsProvider.GetSettingsAsync(ct);
            if (!settings.EnableLearning) return null;

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            context.McpQuerySignals.Add(signal);
            await context.SaveChangesAsync(ct);
            return signal.Id;
        }
        catch (Exception ex)
        {
            // §1.7/§9.5 — signal recording is non-optional. Swallow so a transient DB issue doesn't fail
            // the tool call, but log at Error so a sustained outage of the learning loop is visible.
            logger.LogError(ex, "Failed to record MCP query signal");
            return null;
        }
    }
}
