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
            logger.LogWarning(ex, "Failed to record MCP query signal");
        }
    }
}
