using Moq;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Tests.Common;

/// <summary>
/// Builds a <see cref="Mock{T}"/> of <see cref="IMcpSettingsProvider"/> with EVERY read method set up, so a fixture
/// that only cared about <c>GetSettingsAsync</c> keeps working after a consumer moves to
/// <c>GetEffectiveSettingsAsync</c> — a Moq-default <c>null</c> Task would otherwise fail far from the cause
/// (lesson 2026-09-09, static-helper coupling). <c>GetEffectiveSettingsAsync</c> and the detail call DELEGATE to the
/// mock's own <c>GetSettingsAsync</c>, so a fixture's later <c>Setup(x => x.GetSettingsAsync(...))</c> is honoured on
/// the effective path too — the helper is a drop-in for <c>new Mock&lt;IMcpSettingsProvider&gt;()</c>.
/// <para>
/// <paramref name="projectSettings"/> makes the double PROJECT-AWARE: a project id present in the map gets that
/// project's effective settings, any other id falls back to the global stub. A consumer that resolves the wrong
/// project (or hard-codes 0) then observes a different value and its test fails — without this, a wrong-project
/// regression is invisible to every fixture built on the helper (review findings T-F001 / SF-F001).
/// </para>
/// Returns the mock so fixtures can add <c>Verify</c> calls.
/// </summary>
internal static class SettingsProviderMock
{
    public static Mock<IMcpSettingsProvider> Create(
        McpSettingsData? settings = null,
        McpProjectSettingsData? overrides = null,
        IEnumerable<string>? lockedFields = null,
        IEnumerable<string>? clampedFields = null,
        IReadOnlyDictionary<int, McpSettingsData>? projectSettings = null)
    {
        var locked = new HashSet<string>(lockedFields ?? [], StringComparer.Ordinal);
        var clamped = new HashSet<string>(clampedFields ?? [], StringComparer.Ordinal);

        var mock = new Mock<IMcpSettingsProvider>();
        mock.Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings ?? new McpSettingsData());
        mock.Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int projectId, CancellationToken ct) =>
                projectSettings != null && projectSettings.TryGetValue(projectId, out var perProject)
                    ? Task.FromResult(perProject)
                    : mock.Object.GetSettingsAsync(ct));
        mock.Setup(x => x.GetEffectiveSettingsDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (int projectId, CancellationToken ct) =>
                new McpEffectiveSettings(await mock.Object.GetEffectiveSettingsAsync(projectId, ct), locked, clamped));
        mock.Setup(x => x.GetProjectOverridesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(overrides);

        return mock;
    }
}
