using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.McpSettings;
using McpGlobalSettings = Beacon.Core.Data.Entities.McpSettings;

namespace Beacon.Tests.Unit;

/// <summary>
/// The MCP settings history records the settings named on an explicit list per row type — never a row's id, timestamps or
/// project link, and never a property only because it exists on the row.
/// </summary>
[TestFixture]
public class McpSettingsChangeAuditTests
{
    [Test]
    public void TheGlobalSnapshot_HoldsTheListedSettingsOnly()
    {
        var snapshot = McpSettingsChangeAudit.Snapshot(new McpGlobalSettings { Id = 7, MaxRowLimit = 250, AskSystemPrompt = "Be brief." });

        snapshot.Should().ContainKey(nameof(McpGlobalSettings.MaxRowLimit)).WhoseValue.Should().Be("250");
        snapshot.Should().ContainKey(nameof(McpGlobalSettings.AskSystemPrompt)).WhoseValue.Should().Be("Be brief.");
        snapshot.Should().ContainKey(nameof(McpGlobalSettings.EnforceReadOnly)).WhoseValue.Should().Be("true");
        snapshot.Keys.Should().NotContain([nameof(McpGlobalSettings.Id), nameof(McpGlobalSettings.CreatedTime)]);
        snapshot.Should().HaveCount(41);
    }

    [Test]
    public void TheProjectSnapshot_HoldsTheListedOverridesOnly()
    {
        var snapshot = McpSettingsChangeAudit.Snapshot(new McpProjectSettings { Id = 3, ProjectId = 42, EnablePiiDetection = false });

        snapshot.Should().ContainKey(nameof(McpProjectSettings.EnablePiiDetection)).WhoseValue.Should().Be("false");
        snapshot.Should().ContainKey(nameof(McpProjectSettings.MaxRowLimit)).WhoseValue.Should().BeNull();
        snapshot.Keys.Should().NotContain(
        [
            nameof(McpProjectSettings.Id),
            nameof(McpProjectSettings.CreatedTime),
            nameof(McpProjectSettings.ProjectId),
            nameof(McpProjectSettings.Project)
        ]);
        snapshot.Should().HaveCount(34);
    }

    [Test]
    public void OnlyAChangedListedSetting_BecomesAHistoryRow()
    {
        var before = McpSettingsChangeAudit.Snapshot(new McpGlobalSettings { MaxRowLimit = 100 });
        var after = McpSettingsChangeAudit.Snapshot(new McpGlobalSettings { Id = 9, MaxRowLimit = 200 });

        var changes = McpSettingsChangeAudit.Changes(McpSettingsChangeAudit.GlobalKeyPrefix, before, after, "5", DateTime.UtcNow);

        changes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            SettingKey = "Mcp.MaxRowLimit",
            OldValue = "100",
            NewValue = "200",
            ChangedByUserId = "5"
        });
    }

    [Test]
    public void AnEditPastTheStoredLength_IsStillRecorded_AndOnlyTheStoredCopyIsCut()
    {
        var prefix = new string('a', McpSettingsChangeAudit.MaxValueLength + 10);
        var before = McpSettingsChangeAudit.Snapshot(new McpGlobalSettings { AskSystemPrompt = prefix + "old" });
        var after = McpSettingsChangeAudit.Snapshot(new McpGlobalSettings { AskSystemPrompt = prefix + "new" });

        var changes = McpSettingsChangeAudit.Changes(McpSettingsChangeAudit.GlobalKeyPrefix, before, after, "5", DateTime.UtcNow);

        var change = changes.Should().ContainSingle().Subject;
        change.SettingKey.Should().Be("Mcp.AskSystemPrompt");
        change.OldValue.Should().HaveLength(McpSettingsChangeAudit.MaxValueLength).And.EndWith("…");
        change.NewValue.Should().HaveLength(McpSettingsChangeAudit.MaxValueLength).And.EndWith("…");
    }
}
