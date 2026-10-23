using Beacon.Core.Data;
using Beacon.Core.Handlers.McpSettings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services;

/// <summary>
/// Keeps this process's copies of the stored settings in step with saves made by another process (a second replica).
/// Every save of the admin or MCP settings writes its history rows, so the highest history id is the settings' stamp. At
/// most once per <see cref="CheckInterval"/>, on the next settings read (<see cref="McpSettingsProvider"/>, the admin
/// settings view), the stamp is read; when it moved, the cached admin and MCP settings are dropped and, when the admin
/// settings changed, the stored base URL and LLM configuration are applied again (<see cref="StoredSettingsApplier"/>). The
/// write handlers still clear the caches of the process that saved at once. A failed read leaves the caches as they are
/// until the next check and is logged by exception type only. A process-wide singleton: one check runs at a time, and a
/// read that finds a check running does not wait for it.
/// </summary>
internal sealed class StoredSettingsConvergence(
    IDbContextFactory<BeaconContext> contextFactory,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<StoredSettingsConvergence> logger)
{
    /// <summary>How long a replica can keep settings another replica changed before it checks again.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextCheckTicks;
    private Stamp? _known;

    /// <summary>Checks the stamp when the last check is older than <see cref="CheckInterval"/>; otherwise returns at once.</summary>
    public async Task EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        if (timeProvider.GetUtcNow().UtcTicks < Interlocked.Read(ref _nextCheckTicks))
        {
            return;
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            if (now.UtcTicks < Interlocked.Read(ref _nextCheckTicks))
            {
                return;
            }

            // Set first: the settings read while the stored settings are applied below finds the check done.
            Interlocked.Exchange(ref _nextCheckTicks, (now + CheckInterval).UtcTicks);

            await CheckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "The settings stamp could not be checked ({ExceptionType}); this process keeps its cached settings until the next check",
                ex.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var latest = await ReadLatestAsync(cancellationToken);
        var known = _known;
        if (known != null && known.Latest == latest)
        {
            return;
        }

        // The admin settings' own stamp is read only when something changed: it decides whether the LLM configuration
        // is rebuilt, which an MCP settings change alone never needs.
        var stamp = new Stamp(latest, await ReadLatestAdminAsync(cancellationToken));
        _known = stamp;

        // The first check only records the stamp (this process applied the stored settings when it started), but drops
        // what was cached before it, which a save made while the process started could have outdated.
        AppSettingsService.ResetCache();
        McpSettingsProvider.ResetCache();

        if (known == null || known.LatestAdmin == stamp.LatestAdmin)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var applier = scope.ServiceProvider.GetService<StoredSettingsApplier>();
        if (applier != null)
        {
            await applier.ApplyChangedAsync(cancellationToken);
        }
    }

    private async Task<int?> ReadLatestAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.AppSettingHistory
            .MaxAsync(x => (int?)x.Id, cancellationToken);
    }

    private async Task<int?> ReadLatestAdminAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.AppSettingHistory
            .Where(x => !x.SettingKey.StartsWith(McpSettingsChangeAudit.GlobalKeyPrefix))
            .MaxAsync(x => (int?)x.Id, cancellationToken);
    }

    private sealed record Stamp(int? Latest, int? LatestAdmin);
}
