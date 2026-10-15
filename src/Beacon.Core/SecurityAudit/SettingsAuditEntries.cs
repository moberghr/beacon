using Beacon.Core.Data.Entities;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// One security audit event per settings save that changes something: the keys it changed, the settings-history rows
/// that hold their values before and after (read there by an Auditor or a SecurityAdmin), and a keyed digest of each value
/// before and after, in the order of the keys, so the sealed event shows what each key changed from and to (a history row
/// edited later no longer matches it) without holding a value. The values themselves never go into the event.
/// </summary>
internal static class SettingsAuditEntries
{
    /// <summary>The event of a save that wrote <paramref name="changes"/> (after its save gave them ids); none when it changed nothing.</summary>
    public static IEnumerable<SecurityAuditEntry> Changed(string action, int? projectId, IReadOnlyList<AppSettingHistory> changes)
    {
        if (changes.Count == 0)
        {
            return [];
        }

        // One change per key, in the order the keys are listed, so the digests line up with them.
        var byKey = changes
            .GroupBy(x => x.SettingKey, StringComparer.Ordinal)
            .Select(x => (Key: x.Key, Before: x.First().OldValue, After: x.Last().NewValue))
            .ToList();

        return
        [
            new SecurityAuditEntry(action)
                .On(projectId == null ? SecurityAuditResources.Settings : SecurityAuditResources.Project, projectId)
                .InProject(projectId)
                .With(new SecurityAuditDetails()
                    .AddCodes("changedKeys", byKey.Select(x => x.Key))
                    .Add("historyIds", changes.Select(x => x.Id)))
                .Digesting("beforeDigests", byKey.Select(x => x.Before))
                .Digesting("afterDigests", byKey.Select(x => x.After))
        ];
    }
}
