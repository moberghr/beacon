using Beacon.Core.Data.Entities;
using Beacon.Core.Models;

namespace Beacon.Core.Configuration;

/// <summary>
/// One deployment lock: which <c>Beacon:Mcp</c> option pins which <see cref="McpSettingsData"/> field, and what
/// that means at resolution time (<see cref="Pin"/>), on a global write (<see cref="Contradicts"/>,
/// <see cref="KeepStored"/>) and on a project write (<see cref="HasOverride"/>).
/// </summary>
internal sealed record McpLockRule(
    string FieldName,
    string LockName,
    Func<McpDeploymentOptions, bool> IsActive,
    Action<McpSettingsData> Pin,
    Func<McpSettingsData, bool> Contradicts,
    Func<McpProjectSettingsData, bool> HasOverride,
    Action<McpSettingsData, McpSettings> KeepStored);

/// <summary>
/// The single source of truth for deployment locks. <c>McpSettingsProvider.ApplyLocks</c>, <c>UpdateMcpSettingsHandler</c>
/// and <c>UpdateMcpProjectSettingsHandler</c> all iterate <see cref="ActiveRules"/>, so adding a lockable field is one
/// new entry here — never a new <c>if</c> in a handler. Ceilings are deliberately NOT modelled here: they are applied in
/// <c>McpSettingsProvider.ApplyCeilings</c> and only the global write handler needs ceiling-aware echo detection
/// (<c>KeepStoredWhenClamped</c>), because the global GET returns resolved values while project rows store raw overrides.
/// </summary>
internal static class McpLockPolicy
{
    private static readonly McpLockRule[] All =
    [
        new(
            FieldName: nameof(McpSettingsData.EnforceReadOnly),
            LockName: nameof(McpDeploymentOptions.ForceReadOnly),
            IsActive: x => x.ForceReadOnly,
            Pin: x => x.EnforceReadOnly = true,
            Contradicts: x => !x.EnforceReadOnly,
            HasOverride: x => x.EnforceReadOnly != null,
            KeepStored: (incoming, stored) => incoming.EnforceReadOnly = stored.EnforceReadOnly),
        new(
            FieldName: nameof(McpSettingsData.RetainQueryContent),
            LockName: nameof(McpDeploymentOptions.ForceNoContentRetention),
            IsActive: x => x.ForceNoContentRetention,
            Pin: x => x.RetainQueryContent = false,
            Contradicts: x => x.RetainQueryContent,
            HasOverride: x => x.RetainQueryContent != null,
            KeepStored: (incoming, stored) => incoming.RetainQueryContent = stored.RetainQueryContent)
    ];

    public static IReadOnlyList<McpLockRule> ActiveRules(McpDeploymentOptions options)
    {
        return All
            .Where(x => x.IsActive(options))
            .ToList();
    }
}
