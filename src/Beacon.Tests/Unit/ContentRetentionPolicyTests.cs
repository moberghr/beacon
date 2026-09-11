using FluentAssertions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC4 of spec <c>retention-lock</c>: the policy reads the content lock off the EFFECTIVE settings, so a deployment
/// lock pins every project (including the project-less global row 0) and a per-project override locks only that
/// project. Explicit feedback content can never outlive the query-content lock.
/// </summary>
[TestFixture]
public class ContentRetentionPolicyTests
{
    private const int LockedProjectId = 42;
    private const int OpenProjectId = 7;

    [Test]
    public async Task ForceNoContentRetention_LocksEveryProjectIncludingTheGlobalRow()
    {
        var provider = SettingsProviderMock.Create(
            new McpSettingsData { RetainQueryContent = false, AllowExplicitFeedbackContent = false });
        var policy = new ContentRetentionPolicy(provider.Object);

        var global = await policy.ResolveAsync(null);
        var project = await policy.ResolveAsync(LockedProjectId);

        global.Should().Be(new ContentRetentionDecision(false, false));
        global.Locked.Should().BeTrue();
        project.Should().Be(new ContentRetentionDecision(false, false));
        project.Locked.Should().BeTrue();
    }

    [Test]
    public async Task ProjectOverride_LocksThatProjectOnly()
    {
        var provider = SettingsProviderMock.Create(
            new McpSettingsData { RetainQueryContent = true, AllowExplicitFeedbackContent = true },
            projectSettings: new Dictionary<int, McpSettingsData>
            {
                [LockedProjectId] = new() { RetainQueryContent = false, AllowExplicitFeedbackContent = true }
            });
        var policy = new ContentRetentionPolicy(provider.Object);

        var locked = await policy.ResolveAsync(LockedProjectId);
        var open = await policy.ResolveAsync(OpenProjectId);

        locked.Locked.Should().BeTrue();
        locked.RetainQueryContent.Should().BeFalse();
        open.Locked.Should().BeFalse();
        open.RetainQueryContent.Should().BeTrue();
        open.AllowExplicitFeedbackContent.Should().BeTrue();
        provider.Verify(x => x.GetEffectiveSettingsAsync(LockedProjectId, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(x => x.GetEffectiveSettingsAsync(OpenProjectId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AllowExplicitFeedbackContent_IsFalseWhenQueryContentIsLocked()
    {
        var provider = SettingsProviderMock.Create(
            new McpSettingsData { RetainQueryContent = false, AllowExplicitFeedbackContent = true });
        var policy = new ContentRetentionPolicy(provider.Object);

        var decision = await policy.ResolveAsync(LockedProjectId);

        decision.AllowExplicitFeedbackContent.Should().BeFalse();
    }

    [Test]
    public async Task NullProjectId_ResolvesTheGlobalRow()
    {
        var provider = SettingsProviderMock.Create(new McpSettingsData { RetainQueryContent = true });
        var policy = new ContentRetentionPolicy(provider.Object);

        var decision = await policy.ResolveAsync(null);

        decision.RetainQueryContent.Should().BeTrue();
        provider.Verify(x => x.GetEffectiveSettingsAsync(0, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(
            x => x.GetEffectiveSettingsAsync(It.Is<int>(y => y != 0), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
