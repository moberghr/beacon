using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Models;

namespace Beacon.Core.Services;

public interface IMcpSettingsProvider
{
    /// <summary>Global row with deployment locks and ceilings applied — for consumers that have no project.</summary>
    Task<McpSettingsData> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// The global row as stored, before any deployment lock or ceiling (the code defaults when there is no row) — what the
    /// global settings page edits. No MCP consumer reads it: they read <see cref="GetSettingsAsync"/> or the project's
    /// effective settings. A provider written against 5.0 that does not implement it serves the resolved row instead.
    /// </summary>
    Task<McpSettingsData> GetStoredSettingsAsync(CancellationToken ct = default) => GetSettingsAsync(ct);

    /// <summary>Resolved for one project: deployment lock → project value → global value → code default, clamped to the ceilings.</summary>
    Task<McpSettingsData> GetEffectiveSettingsAsync(int projectId, CancellationToken ct = default);

    /// <summary>Same as <see cref="GetEffectiveSettingsAsync"/> plus which fields a lock pinned and which a ceiling lowered.</summary>
    Task<McpEffectiveSettings> GetEffectiveSettingsDetailAsync(int projectId, CancellationToken ct = default);

    /// <summary>The project's stored overrides (nulls = inherit), or null when the project has no row.</summary>
    Task<McpProjectSettingsData?> GetProjectOverridesAsync(int projectId, CancellationToken ct = default);

    void InvalidateCache();
}

internal sealed class McpSettingsProvider(
    IDbContextFactory<BeaconContext> contextFactory,
    IMemoryCache cache,
    IOptions<McpDeploymentOptions> deploymentOptions,
    ILogger<McpSettingsProvider> logger) : IMcpSettingsProvider
{
    private const string GlobalCacheKey = "McpSettings";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    // One reset token for every cache entry this provider creates. STATIC because the provider is registered
    // transient (ServiceConfiguration): an instance field would only ever invalidate the entries the same
    // instance created, so a write on one request would leave every other cached copy stale.
    private static CancellationTokenSource _resetSource = new();
    private static readonly object ResetLock = new();

    public async Task<McpSettingsData> GetSettingsAsync(CancellationToken ct = default)
    {
        var global = await GetGlobalRawAsync(ct);

        return Resolve(global, null, deploymentOptions.Value).Effective;
    }

    public async Task<McpSettingsData> GetStoredSettingsAsync(CancellationToken ct = default)
    {
        var global = await GetGlobalRawAsync(ct);

        // A copy: the cached row is shared by every reader.
        return global.Clone();
    }

    public async Task<McpSettingsData> GetEffectiveSettingsAsync(int projectId, CancellationToken ct = default)
    {
        var detail = await GetEffectiveSettingsDetailAsync(projectId, ct);

        return detail.Effective;
    }

    public async Task<McpEffectiveSettings> GetEffectiveSettingsDetailAsync(int projectId, CancellationToken ct = default)
    {
        var global = await GetGlobalRawAsync(ct);
        var project = projectId > 0 ? await GetProjectRawAsync(projectId, ct) : null;

        return Resolve(global, project, deploymentOptions.Value);
    }

    public Task<McpProjectSettingsData?> GetProjectOverridesAsync(int projectId, CancellationToken ct = default)
    {
        return projectId > 0
            ? GetProjectRawAsync(projectId, ct)
            : Task.FromResult<McpProjectSettingsData?>(null);
    }

    public void InvalidateCache()
    {
        CancellationTokenSource previous;
        lock (ResetLock)
        {
            previous = _resetSource;
            _resetSource = new CancellationTokenSource();
        }

        // Cancel but never Dispose: an entry whose factory captured the previous token and commits after this
        // point still registers its expiration callback on it (ConfigurationReloadToken pattern). The
        // cancelled source is collected once the cache drops its callbacks.
        previous.Cancel();
    }

    /// <summary>
    /// Pure resolution: code defaults ← global row ← non-null project overrides ← ceilings (lower only) ← locks
    /// (always last, so no layer can override them). Internal so the precedence tests can drive it directly.
    /// </summary>
    internal static McpEffectiveSettings Resolve(McpSettingsData global, McpProjectSettingsData? project, McpDeploymentOptions options)
    {
        var effective = global.Clone();
        var locked = new HashSet<string>(StringComparer.Ordinal);
        var clamped = new HashSet<string>(StringComparer.Ordinal);

        if (project != null)
        {
            McpSettingFields.Overlay(effective, project, clamped);
        }

        UseDefaultsForUnusableValues(effective, clamped);

        ApplyCeilings(effective, options.Ceilings ?? new McpCeilingOptions(), clamped);

        // A stored row cap below one row (written before the write handlers refused it) is one row: every tool that
        // reads the effective settings gets the same cap, never an uncapped read or a refusal of the cap itself.
        effective.MaxRowLimit = Math.Max(1, effective.MaxRowLimit);

        ApplyLocks(effective, options, locked);

        return new McpEffectiveSettings(effective, locked, clamped);
    }

    // A stored global value no reader can use is the built-in default: a statement timeout of 0 or less would cancel every
    // statement at once, a concurrency of 0 refuse every call, a result budget of 0 return no rows. A timeout above the
    // longest one saved is read as that. Every substituted field is reported as clamped.
    private static void UseDefaultsForUnusableValues(McpSettingsData effective, HashSet<string> clamped)
    {
        var defaults = new McpSettingsData();
        effective.StatementTimeoutSeconds = Usable(effective.StatementTimeoutSeconds, defaults.StatementTimeoutSeconds, McpSettingsRanges.StatementTimeoutSecondsMaximum, nameof(effective.StatementTimeoutSeconds), clamped);
        effective.MaxResultBytes = Usable(effective.MaxResultBytes, defaults.MaxResultBytes, int.MaxValue, nameof(effective.MaxResultBytes), clamped);
        effective.MaxConcurrentQueriesPerKey = Usable(effective.MaxConcurrentQueriesPerKey, defaults.MaxConcurrentQueriesPerKey, int.MaxValue, nameof(effective.MaxConcurrentQueriesPerKey), clamped);
    }

    private static int Usable(int value, int fallback, int maximum, string field, HashSet<string> clamped)
    {
        if (value >= 1 && value <= maximum)
        {
            return value;
        }

        clamped.Add(field);

        return value < 1 ? fallback : maximum;
    }

    private static void ApplyCeilings(McpSettingsData effective, McpCeilingOptions ceilings, HashSet<string> clamped)
    {
        effective.MaxRowLimit = Clamp(effective.MaxRowLimit, ceilings.MaxRowLimit, nameof(effective.MaxRowLimit), clamped);
        effective.StatementTimeoutSeconds = Clamp(effective.StatementTimeoutSeconds, ceilings.StatementTimeoutSeconds, nameof(effective.StatementTimeoutSeconds), clamped);
        effective.MaxResultBytes = Clamp(effective.MaxResultBytes, ceilings.MaxResultBytes, nameof(effective.MaxResultBytes), clamped);
        effective.MaxConcurrentQueriesPerKey = Clamp(effective.MaxConcurrentQueriesPerKey, ceilings.MaxConcurrentQueriesPerKey, nameof(effective.MaxConcurrentQueriesPerKey), clamped);

        // A null MaxExplainCost means "no limit", which is above any ceiling — the ceiling becomes the value.
        if (ceilings.MaxExplainCost is { } costCeiling && (effective.MaxExplainCost == null || effective.MaxExplainCost > costCeiling))
        {
            effective.MaxExplainCost = costCeiling;
            clamped.Add(nameof(effective.MaxExplainCost));
        }
    }

    private static int Clamp(int value, int? ceiling, string field, HashSet<string> clamped)
    {
        if (ceiling is { } max && value > max)
        {
            clamped.Add(field);

            return max;
        }

        return value;
    }

    private static void ApplyLocks(McpSettingsData effective, McpDeploymentOptions options, HashSet<string> locked)
    {
        // One table drives resolution AND both write handlers (McpLockPolicy) — a new lockable field is one entry there.
        foreach (var rule in McpLockPolicy.ActiveRules(options))
        {
            // A refusal the read-only lock causes names the lock (SqlGateRequest.ReadOnlyPinnedByLock).
            if (rule.FieldName == nameof(McpSettingsData.EnforceReadOnly) && rule.Contradicts(effective))
            {
                effective.ReadOnlyPinnedByLock = true;
            }

            rule.Pin(effective);
            locked.Add(rule.FieldName);
        }
    }

    private async Task<McpSettingsData> GetGlobalRawAsync(CancellationToken ct)
    {
        return await cache.GetOrCreateAsync(GlobalCacheKey, async entry =>
        {
            ConfigureEntry(entry);

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var entity = await context.McpSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            return entity != null ? McpSettingFields.ToSettings(entity, x => ParsePiiPatterns(x, "global", entity.Id)) : new McpSettingsData();
        }) ?? new McpSettingsData();
    }

    private async Task<McpProjectSettingsData?> GetProjectRawAsync(int projectId, CancellationToken ct)
    {
        return await cache.GetOrCreateAsync($"{GlobalCacheKey}:{projectId}", async entry =>
        {
            ConfigureEntry(entry);

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var entity = await context.McpProjectSettings
                .Where(x => x.ProjectId == projectId)
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            return entity != null ? McpSettingFields.ToOverrides(entity, x => ParsePiiPatterns(x, "project", entity.Id)) : null;
        });
    }

    private static void ConfigureEntry(ICacheEntry entry)
    {
        entry.AbsoluteExpirationRelativeToNow = CacheDuration;
        entry.AddExpirationToken(new CancellationChangeToken(_resetSource.Token));
    }

    private List<string>? ParsePiiPatterns(string? json, string owner, int id)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException ex)
        {
            // A corrupt row must not silently drop protection; log the identifier (never the payload) so the
            // misconfiguration is discoverable. Global falls back to "no custom patterns", a project row falls
            // back to inheriting the global list.
            logger.LogError(ex, "Failed to deserialize CustomPiiPatterns for {Owner} settings row {SettingsId}; custom PII patterns are disabled until fixed.", owner, id);

            return null;
        }
    }
}
