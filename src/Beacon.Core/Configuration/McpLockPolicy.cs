using Beacon.Core.Data.Entities;
using Beacon.Core.Models;

namespace Beacon.Core.Configuration;

/// <summary>
/// One deployment lock: which <c>Beacon:Mcp</c> option pins which <see cref="McpSettingsData"/> field, and what
/// that means at resolution time (<see cref="Pin"/>), on a global write (<see cref="Contradicts"/>,
/// <see cref="KeepStored"/>), on a project write (<see cref="HasOverride"/>, <see cref="KeepStoredOverride"/>) and for a
/// stored project override (<see cref="OverrideContradicts"/>). A write under the lock keeps the stored value of the field
/// it pins, globally and per project, so lifting the lock restores what was configured before.
/// </summary>
internal sealed record McpLockRule(
    string FieldName,
    string LockName,
    Func<McpDeploymentOptions, bool> IsActive,
    Action<McpSettingsData> Pin,
    Func<McpSettingsData, bool> Contradicts,
    Func<McpProjectSettingsData, bool> HasOverride,
    Func<McpProjectSettingsData, bool> OverrideContradicts,
    Action<McpSettingsData, McpSettings> KeepStored,
    Action<McpProjectSettingsData, McpProjectSettings> KeepStoredOverride);

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
            OverrideContradicts: x => x.EnforceReadOnly == false,
            KeepStored: (incoming, stored) => incoming.EnforceReadOnly = stored.EnforceReadOnly,
            KeepStoredOverride: (incoming, stored) => incoming.EnforceReadOnly = stored.EnforceReadOnly),
        new(
            FieldName: nameof(McpSettingsData.EnablePiiDetection),
            LockName: nameof(McpDeploymentOptions.ForcePiiDetection),
            IsActive: x => x.ForcePiiDetection,
            Pin: x => x.EnablePiiDetection = true,
            Contradicts: x => !x.EnablePiiDetection,
            HasOverride: x => x.EnablePiiDetection != null,
            OverrideContradicts: x => x.EnablePiiDetection == false,
            KeepStored: (incoming, stored) => incoming.EnablePiiDetection = stored.EnablePiiDetection,
            KeepStoredOverride: (incoming, stored) => incoming.EnablePiiDetection = stored.EnablePiiDetection),
        new(
            FieldName: nameof(McpSettingsData.RetainQueryContent),
            LockName: nameof(McpDeploymentOptions.ForceNoContentRetention),
            IsActive: x => x.ForceNoContentRetention,
            Pin: x => x.RetainQueryContent = false,
            Contradicts: x => x.RetainQueryContent,
            HasOverride: x => x.RetainQueryContent != null,
            OverrideContradicts: x => x.RetainQueryContent == true,
            KeepStored: (incoming, stored) => incoming.RetainQueryContent = stored.RetainQueryContent,
            KeepStoredOverride: (incoming, stored) => incoming.RetainQueryContent = stored.RetainQueryContent),

        // One lock, three fields: every setting that sends row values read from a data source to the LLM provider.
        new(
            FieldName: nameof(McpSettingsData.EnableSampleValueCollection),
            LockName: nameof(McpDeploymentOptions.ForbidRowDataToLlm),
            IsActive: x => x.ForbidRowDataToLlm,
            Pin: x => x.EnableSampleValueCollection = false,
            Contradicts: x => x.EnableSampleValueCollection,
            HasOverride: x => x.EnableSampleValueCollection != null,
            OverrideContradicts: x => x.EnableSampleValueCollection == true,
            KeepStored: (incoming, stored) => incoming.EnableSampleValueCollection = stored.EnableSampleValueCollection,
            KeepStoredOverride: (incoming, stored) => incoming.EnableSampleValueCollection = stored.EnableSampleValueCollection),
        new(
            FieldName: nameof(McpSettingsData.EnableValueGrounding),
            LockName: nameof(McpDeploymentOptions.ForbidRowDataToLlm),
            IsActive: x => x.ForbidRowDataToLlm,
            Pin: x => x.EnableValueGrounding = false,
            Contradicts: x => x.EnableValueGrounding,
            HasOverride: x => x.EnableValueGrounding != null,
            OverrideContradicts: x => x.EnableValueGrounding == true,
            KeepStored: (incoming, stored) => incoming.EnableValueGrounding = stored.EnableValueGrounding,
            KeepStoredOverride: (incoming, stored) => incoming.EnableValueGrounding = stored.EnableValueGrounding),
        new(
            FieldName: nameof(McpSettingsData.EnableEvalJudge),
            LockName: nameof(McpDeploymentOptions.ForbidRowDataToLlm),
            IsActive: x => x.ForbidRowDataToLlm,
            Pin: x => x.EnableEvalJudge = false,
            Contradicts: x => x.EnableEvalJudge,
            HasOverride: x => x.EnableEvalJudge != null,
            OverrideContradicts: x => x.EnableEvalJudge == true,
            KeepStored: (incoming, stored) => incoming.EnableEvalJudge = stored.EnableEvalJudge,
            KeepStoredOverride: (incoming, stored) => incoming.EnableEvalJudge = stored.EnableEvalJudge)
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
