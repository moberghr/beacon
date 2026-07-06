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
    public async Task RecordSignalAsync(McpQuerySignal signal, CancellationToken ct = default)
    {
        try
        {
            var settings = await settingsProvider.GetSettingsAsync(ct);
            if (!settings.EnableLearning) return;

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            context.McpQuerySignals.Add(signal);
            await context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // §1.7/§9.5 — signal recording is non-optional. Swallow so a transient DB issue doesn't fail
            // the tool call, but log at Error so a sustained outage of the learning loop is visible.
            logger.LogError(ex, "Failed to record MCP query signal");
        }
    }
}
