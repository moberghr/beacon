using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Services;

public class AppSettingsService : IAppSettingsService, IAppSettingsStager
{
    private const string CacheKey = "AppSettings_All";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<BeaconContext> _contextFactory;
    private readonly IMemoryCache _cache;
    private readonly IEncryptionService _encryption;
    private readonly BeaconConfiguration _config;
    private readonly ILlmConfigurationUpdater? _llmUpdater;
    private readonly ISecurityAuditRecorder _securityAudit;

    public AppSettingsService(
        IDbContextFactory<BeaconContext> contextFactory,
        IMemoryCache cache,
        IEncryptionService encryption,
        BeaconConfiguration config,
        ISecurityAuditRecorder securityAudit,
        ILlmConfigurationUpdater? llmUpdater = null)
    {
        _contextFactory = contextFactory;
        _cache = cache;
        _encryption = encryption;
        _config = config;
        _securityAudit = securityAudit;
        _llmUpdater = llmUpdater;
    }

    public async Task<AppSettingsData> GetSettingsAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out AppSettingsData? cached) && cached != null)
            return cached;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        var data = await ReadAsync(context, ct);

        _cache.Set(CacheKey, data, CacheDuration);
        return data;
    }

    Task<AppSettingsData> IAppSettingsStager.ReadStoredAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        return ReadAsync(context, cancellationToken);
    }

    // The stored settings as the context reads them now (never the cache), sensitive values decrypted.
    private async Task<AppSettingsData> ReadAsync(BeaconContext context, CancellationToken ct)
    {
        var rows = await context.AppSettings.AsNoTracking().ToListAsync(ct);

        var dict = rows.ToDictionary(r => r.Key, r => new { r.Value, r.IsSensitive });

        return new AppSettingsData
        {
            BaseUrl = GetValue("General.BaseUrl"),
            LlmProvider = GetEnum<AiProvider>("LLM.Provider"),
            LlmApiKey = GetValue("LLM.ApiKey"),
            LlmEndpoint = GetValue("LLM.Endpoint"),
            LlmRegion = GetValue("LLM.Region"),
            LlmSessionToken = GetValue("LLM.SessionToken"),
            LlmAwsAccessKeyId = GetValue("LLM.AwsAccessKeyId"),
            LlmAwsSecretAccessKey = GetValue("LLM.AwsSecretAccessKey"),
            LlmBedrockAuthMode = GetEnum<BedrockAuthMode>("LLM.BedrockAuthMode") ?? BedrockAuthMode.IamRole,
            LlmModel = GetValue("LLM.Model"),
            LlmFastModel = GetValue("LLM.FastModel"),
            LlmMaxConcurrentRequests = GetInt("LLM.MaxConcurrentRequests", 50),
            LlmTokensPerMinute = GetInt("LLM.TokensPerMinute", 80000),
            LlmRequestsPerMinute = GetInt("LLM.RequestsPerMinute", 1000),
            LlmMonthlyBudget = GetDecimal("LLM.MonthlyBudget", 100.00m),
            LlmRateLimitsEnabled = GetBool("LLM.RateLimitsEnabled"),
            LlmLimitsStored = dict.ContainsKey("LLM.MaxConcurrentRequests")
                || dict.ContainsKey("LLM.TokensPerMinute")
                || dict.ContainsKey("LLM.RequestsPerMinute")
                || dict.ContainsKey("LLM.RateLimitsEnabled"),
        };

        string? GetValue(string key)
        {
            if (!dict.TryGetValue(key, out var entry) || entry.Value == null)
                return null;
            return entry.IsSensitive ? _encryption.Decrypt(entry.Value) : entry.Value;
        }

        int GetInt(string key, int defaultValue = 0)
        {
            var v = GetValue(key);
            return v != null && int.TryParse(v, out var i) ? i : defaultValue;
        }

        bool GetBool(string key)
        {
            var v = GetValue(key);
            return v != null && bool.TryParse(v, out var b) && b;
        }

        decimal GetDecimal(string key, decimal defaultValue)
        {
            var v = GetValue(key);
            return v != null && decimal.TryParse(v, CultureInfo.InvariantCulture, out var d) ? d : defaultValue;
        }

        T? GetEnum<T>(string key) where T : struct, Enum
        {
            var v = GetValue(key);
            return v != null && Enum.TryParse<T>(v, true, out var e) ? e : null;
        }

    }

    public Task SaveSettingsAsync(AppSettingsData settings, string? userId = null, CancellationToken ct = default)
    {
        return SaveSettingsAsync(settings, userId, saveLimits: true, ct);
    }

    public async Task SaveSettingsAsync(AppSettingsData settings, string? userId, bool saveLimits, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        var staged = await StageAsync(context, settings, userId, AppSettingsParts.Everything, saveLimits, ct);

        // One security audit event per save that changed something, naming the keys and their history rows.
        await _securityAudit.SaveWithEventsAsync(context, staged.AuditEntries, ct);

        await staged.AfterCommitAsync(ct);
    }

    Task<StagedAppSettings> IAppSettingsStager.StageAsync(
        BeaconContext context,
        AppSettingsData settings,
        string? changedByUserId,
        AppSettingsParts parts,
        bool saveLimits,
        CancellationToken cancellationToken)
    {
        return StageAsync(context, settings, changedByUserId, parts, saveLimits, cancellationToken);
    }

    // The rows and history of a save of the parts given, added to the context unsaved; what follows the commit is the
    // staged save's AfterCommitAsync.
    private async Task<StagedAppSettings> StageAsync(
        BeaconContext context,
        AppSettingsData settings,
        string? userId,
        AppSettingsParts parts,
        bool saveLimits,
        CancellationToken ct)
    {
        var rows = await context.AppSettings.ToListAsync(ct);
        var dict = rows.ToDictionary(r => r.Key);
        var historyEntries = new List<AppSettingHistory>();
        var now = DateTime.UtcNow;

        void SetValue(string key, string? newValue, bool isSensitive, string category)
        {
            if (!dict.TryGetValue(key, out var row))
            {
                // Row doesn't exist yet — insert it. Without this the entire
                // admin settings UI silently no-ops on a fresh database.
                if (newValue == null)
                {
                    return;
                }

                var stored = isSensitive ? _encryption.Encrypt(newValue) : newValue;
                var inserted = new AppSetting
                {
                    Key = key,
                    Value = stored,
                    IsSensitive = isSensitive,
                    Category = category,
                };
                context.AppSettings.Add(inserted);
                dict[key] = inserted;

                historyEntries.Add(new AppSettingHistory
                {
                    SettingKey = key,
                    OldValue = null,
                    NewValue = isSensitive ? "***" : newValue,
                    ChangedAt = now,
                    ChangedByUserId = userId,
                });
                return;
            }

            // Decrypt current value for comparison
            var currentPlain = row.IsSensitive && row.Value != null
                ? _encryption.Decrypt(row.Value)
                : row.Value;

            if (currentPlain == newValue)
            {
                return;
            }

            historyEntries.Add(new AppSettingHistory
            {
                SettingKey = key,
                OldValue = isSensitive ? "***" : currentPlain,
                NewValue = isSensitive ? "***" : newValue,
                ChangedAt = now,
                ChangedByUserId = userId,
            });

            row.Value = isSensitive && newValue != null ? _encryption.Encrypt(newValue) : newValue;
        }

        if (parts != AppSettingsParts.ConnectionOnly)
        {
            SetValue("General.BaseUrl", settings.BaseUrl, false, "General");
        }

        // The LLM connection and its models: left as stored while a change of the connection waits for approval.
        if (parts != AppSettingsParts.AllButConnection)
        {
            SetValue("LLM.Provider", settings.LlmProvider?.ToString(), false, "LLM");
            SetValue("LLM.ApiKey", settings.LlmApiKey, true, "LLM");
            SetValue("LLM.Endpoint", settings.LlmEndpoint, true, "LLM");
            SetValue("LLM.Region", settings.LlmRegion, false, "LLM");
            SetValue("LLM.SessionToken", settings.LlmSessionToken, true, "LLM");
            SetValue("LLM.AwsAccessKeyId", settings.LlmAwsAccessKeyId, true, "LLM");
            SetValue("LLM.AwsSecretAccessKey", settings.LlmAwsSecretAccessKey, true, "LLM");
            SetValue("LLM.BedrockAuthMode", settings.LlmBedrockAuthMode.ToString(), false, "LLM");
            SetValue("LLM.Model", settings.LlmModel, false, "LLM");
            SetValue("LLM.FastModel", settings.LlmFastModel, false, "LLM");
        }

        if (parts != AppSettingsParts.ConnectionOnly)
        {
            SetValue("LLM.MonthlyBudget", settings.LlmMonthlyBudget.ToString(CultureInfo.InvariantCulture), false, "LLM");
        }

        // The limits are an Admin's: anyone else's save leaves the stored ones (or the host's, when none are stored) alone.
        if (saveLimits && parts != AppSettingsParts.ConnectionOnly)
        {
            SetValue("LLM.MaxConcurrentRequests", settings.LlmMaxConcurrentRequests.ToString(), false, "LLM");
            SetValue("LLM.TokensPerMinute", settings.LlmTokensPerMinute.ToString(), false, "LLM");
            SetValue("LLM.RequestsPerMinute", settings.LlmRequestsPerMinute.ToString(), false, "LLM");
            SetValue("LLM.RateLimitsEnabled", settings.LlmRateLimitsEnabled.ToString(), false, "LLM");
        }

        if (historyEntries.Count > 0)
        {
            context.AppSettingHistory.AddRange(historyEntries);
        }

        var applyLimits = saveLimits && parts != AppSettingsParts.ConnectionOnly;

        return new StagedAppSettings(historyEntries, async cancellationToken =>
        {
            // Invalidate cache
            _cache.Remove(CacheKey);

            if (parts != AppSettingsParts.ConnectionOnly)
            {
                // Mutate BeaconConfiguration singleton
                _config.BaseUrl = settings.BaseUrl;
            }

            // A save of part of the settings applies them as they are now stored, read again after the commit.
            var applied = parts == AppSettingsParts.Everything ? settings : await GetSettingsAsync(cancellationToken);

            // Update LLM configuration if updater is available; the limits in force change only with an Admin's save.
            if (applyLimits)
            {
                _llmUpdater?.UpdateConfiguration(applied);
            }
            else
            {
                _llmUpdater?.UpdateConfiguration(applied, applyLimits: false);
            }
        });
    }

    public async Task<List<AppSettingHistory>> GetHistoryAsync(string? key = null, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        var query = context.AppSettingHistory.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(key))
            query = query.Where(h => h.SettingKey == key);

        // The fields a caller reads, never the seal columns: the history reads before the seal migration is applied too.
        return await query
            .OrderByDescending(x => x.ChangedAt)
            .Take(200)
            .Select(x =>
                new AppSettingHistory
                {
                    Id = x.Id,
                    CreatedTime = x.CreatedTime,
                    SettingKey = x.SettingKey,
                    OldValue = x.OldValue,
                    NewValue = x.NewValue,
                    ChangedAt = x.ChangedAt,
                    ChangedByUserId = x.ChangedByUserId
                })
            .ToListAsync(ct);
    }
}

/// <summary>Which settings a staged save writes (<see cref="IAppSettingsStager"/>).</summary>
internal enum AppSettingsParts
{
    /// <summary>Every setting, as <see cref="IAppSettingsService.SaveSettingsAsync(AppSettingsData, string?, bool, CancellationToken)"/> writes them.</summary>
    Everything = 1,

    /// <summary>Every setting but the LLM connection (provider, endpoint, region, authentication mode, credentials) and its models.</summary>
    AllButConnection = 2,

    /// <summary>The LLM connection and its models only, never the limits.</summary>
    ConnectionOnly = 3
}

/// <summary>
/// Writes admin settings inside a unit of work its caller owns: a change of the LLM connection staged for approval saves the
/// other settings with its request, and its approval writes the connection with the decision. A caller that reads or writes
/// the LLM connection holds the LLM connection lock (<see cref="ChangeApprovals.ILlmConnectionLock"/>) in that unit of work
/// before it reads. Implemented by <see cref="AppSettingsService"/>.
/// </summary>
internal interface IAppSettingsStager
{
    /// <summary>The stored settings as <paramref name="context"/> reads them now (never a cached copy).</summary>
    Task<AppSettingsData> ReadStoredAsync(BeaconContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the setting rows and history of a save of <paramref name="parts"/> of <paramref name="settings"/> to
    /// <paramref name="context"/>, unsaved, the history attributed to <paramref name="changedByUserId"/>; the limits only
    /// with <paramref name="saveLimits"/> (never with <see cref="AppSettingsParts.ConnectionOnly"/>). The caller saves with
    /// <see cref="StagedAppSettings.AuditEntries"/> and, after the commit, calls <see cref="StagedAppSettings.AfterCommitAsync"/>.
    /// </summary>
    Task<StagedAppSettings> StageAsync(
        BeaconContext context,
        AppSettingsData settings,
        string? changedByUserId,
        AppSettingsParts parts,
        bool saveLimits,
        CancellationToken cancellationToken);
}

/// <summary>
/// A staged save of admin settings: its history rows, the security audit event they make (once the save gave them ids), and
/// what follows the commit (the settings cache cleared, the base URL and the LLM configuration applied).
/// </summary>
internal sealed class StagedAppSettings(IReadOnlyList<AppSettingHistory> history, Func<CancellationToken, Task> afterCommit)
{
    public IReadOnlyList<AppSettingHistory> History => history;

    /// <summary>The <c>AdminSettingsChanged</c> event of the save; none when it changed nothing.</summary>
    public IEnumerable<SecurityAuditEntry> AuditEntries()
    {
        return SettingsAuditEntries.Changed(SecurityAuditActions.AdminSettingsChanged, null, history);
    }

    /// <summary>After the commit: clears the settings cache and applies what is now stored.</summary>
    public Task AfterCommitAsync(CancellationToken cancellationToken)
    {
        return afterCommit(cancellationToken);
    }
}
