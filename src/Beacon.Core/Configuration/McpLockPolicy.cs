using Beacon.Core.Models;

namespace Beacon.Core.Configuration;

/// <summary>
/// One deployment lock: which <c>Beacon:Mcp</c> option pins which boolean <see cref="McpSettingsData"/> field to which
/// value, and what that means at resolution time (<see cref="Pin"/>), on a global write (<see cref="Contradicts"/>), on a
/// project write (<see cref="HasOverride"/>) and for a stored project override (<see cref="OverrideContradicts"/>). A write
/// under the lock does not write the field it pins (the handlers leave it out of <c>McpSettingFields.Store</c>), globally
/// and per project, so lifting the lock restores what was configured before.
/// </summary>
internal sealed record McpLockRule(
    string FieldName,
    string LockName,
    Func<McpDeploymentOptions, bool> IsActive,
    bool PinnedValue)
{
    private McpSettingField Field => McpSettingFields.Named(FieldName);

    public void Pin(McpSettingsData settings) => Field.Set(settings, PinnedValue);

    public bool Contradicts(McpSettingsData settings) => Field.Get(settings) is bool value && value != PinnedValue;

    public bool HasOverride(McpProjectSettingsData overrides) => Field.GetOverride(overrides) != null;

    public bool OverrideContradicts(McpProjectSettingsData overrides) => Field.GetOverride(overrides) is bool value && value != PinnedValue;
}

/// <summary>
/// The single source of truth for deployment locks. <c>McpSettingsProvider.ApplyLocks</c>, <c>UpdateMcpSettingsHandler</c>
/// and <c>UpdateMcpProjectSettingsHandler</c> all iterate <see cref="ActiveRules"/>, so adding a lockable field is one
/// new entry here — never a new <c>if</c> in a handler. Ceilings are deliberately NOT modelled here: they are applied in
/// <c>McpSettingsProvider.ApplyCeilings</c>, and a write stores a value above one as sent (the global GET returns the
/// stored values next to the resolved ones, so the page edits what is stored).
/// </summary>
internal static class McpLockPolicy
{
    /// <summary>The name <c>Beacon:Mcp:Audit:RequireContent</c> goes by in refusals, the locks list and startup warnings.</summary>
    internal const string RequireContentLockName = "Audit:" + nameof(McpAuditOptions.RequireContent);

    private static readonly McpLockRule[] All =
    [
        new(
            FieldName: nameof(McpSettingsData.EnforceReadOnly),
            LockName: nameof(McpDeploymentOptions.ForceReadOnly),
            IsActive: x => x.ForceReadOnly,
            PinnedValue: true),
        new(
            FieldName: nameof(McpSettingsData.EnablePiiDetection),
            LockName: nameof(McpDeploymentOptions.ForcePiiDetection),
            IsActive: x => x.ForcePiiDetection,
            PinnedValue: true),
        new(
            FieldName: nameof(McpSettingsData.RetainQueryContent),
            LockName: nameof(McpDeploymentOptions.ForceNoContentRetention),
            IsActive: x => x.ForceNoContentRetention,
            PinnedValue: false),

        // The audit's content can't be switched off unseen: pinned on, the opposite of the lock above (both on fails startup).
        new(
            FieldName: nameof(McpSettingsData.RetainQueryContent),
            LockName: RequireContentLockName,
            IsActive: x => x.Audit?.RequireContent == true,
            PinnedValue: true),

        // One lock, three fields: every setting that sends row values read from a data source to the LLM provider.
        new(
            FieldName: nameof(McpSettingsData.EnableSampleValueCollection),
            LockName: nameof(McpDeploymentOptions.ForbidRowDataToLlm),
            IsActive: x => x.ForbidRowDataToLlm,
            PinnedValue: false),
        new(
            FieldName: nameof(McpSettingsData.EnableValueGrounding),
            LockName: nameof(McpDeploymentOptions.ForbidRowDataToLlm),
            IsActive: x => x.ForbidRowDataToLlm,
            PinnedValue: false),
        new(
            FieldName: nameof(McpSettingsData.EnableEvalJudge),
            LockName: nameof(McpDeploymentOptions.ForbidRowDataToLlm),
            IsActive: x => x.ForbidRowDataToLlm,
            PinnedValue: false)
    ];

    public static IReadOnlyList<McpLockRule> ActiveRules(McpDeploymentOptions options)
    {
        return All
            .Where(x => x.IsActive(options))
            .ToList();
    }

    /// <summary>
    /// The stored settings rows an active lock overrides — a value the lock pins the other way, which resolution silently
    /// replaces: the global rows by id, the project rows by project id (an empty, inherited override never counts), and the
    /// names of the locks concerned.
    /// </summary>
    public static McpLockOverrides Overridden(
        McpDeploymentOptions options,
        IEnumerable<(int Id, McpSettingsData Stored)> globalRows,
        IEnumerable<(int ProjectId, McpProjectSettingsData Overrides)> projectRows)
    {
        var rules = ActiveRules(options);
        var globals = globalRows
            .Select(x => (x.Id, Locks: rules.Where(y => y.Contradicts(x.Stored)).ToList()))
            .Where(x => x.Locks.Count > 0)
            .ToList();
        var projects = projectRows
            .Select(x => (x.ProjectId, Locks: rules.Where(y => y.OverrideContradicts(x.Overrides)).ToList()))
            .Where(x => x.Locks.Count > 0)
            .ToList();

        return new McpLockOverrides(
            globals
                .Select(x => x.Id)
                .Order()
                .ToList(),
            projects
                .Select(x => x.ProjectId)
                .Order()
                .ToList(),
            globals
                .SelectMany(x => x.Locks)
                .Concat(projects.SelectMany(x => x.Locks))
                .Select(x => x.LockName)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList());
    }
}

/// <summary>What <see cref="McpLockPolicy.Overridden"/> found: the stored rows a deployment lock overrides, by id.</summary>
internal sealed record McpLockOverrides(
    IReadOnlyList<int> GlobalRowIds,
    IReadOnlyList<int> ProjectIds,
    IReadOnlyList<string> LockNames)
{
    public bool Any => GlobalRowIds.Count > 0 || ProjectIds.Count > 0;
}
