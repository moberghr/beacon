using Beacon.Core.Data.Entities;
using Beacon.Core.Models.Settings;

namespace Beacon.Core.Services;

public interface IAppSettingsService
{
    Task<AppSettingsData> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettingsData settings, string? userId = null, CancellationToken ct = default);

    /// <summary>
    /// Saves the settings. With <paramref name="saveLimits"/> false (a save by anyone but an Admin) the LLM concurrency
    /// limit, the requests and tokens per minute and whether those are enforced are neither stored nor applied, whatever
    /// <paramref name="settings"/> carries: the stored and running limits stay as they are. An implementation that does
    /// not override this refuses such a save rather than store the limits.
    /// </summary>
    Task SaveSettingsAsync(AppSettingsData settings, string? userId, bool saveLimits, CancellationToken ct = default)
    {
        return saveLimits
            ? SaveSettingsAsync(settings, userId, ct)
            : throw new NotSupportedException("This settings service cannot save the settings without the LLM limits.");
    }

    Task<List<AppSettingHistory>> GetHistoryAsync(string? key = null, CancellationToken ct = default);
}
