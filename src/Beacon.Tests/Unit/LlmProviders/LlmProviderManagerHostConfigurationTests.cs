using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// Settings saved without a provider keep the host-configured provider (<c>Beacon:LLM</c>) in force, as after a restart:
/// only the limits (an Admin's save) or the recorded budget follow the settings.
/// </summary>
[TestFixture]
public class LlmProviderManagerHostConfigurationTests
{
    private static readonly LlmConfiguration Host = new()
    {
        Provider = AiProvider.OpenAI,
        ApiKey = "host-key",
        Endpoint = "https://llm.host.example",
        Model = "gpt-host",
        FastModel = "gpt-host-mini",
        Limits = new ProviderLimits { MaxConcurrentRequests = 10 }
    };

    [Test]
    public void SaveWithoutAProvider_KeepsTheHostConfiguredProvider_AndAppliesTheLimits()
    {
        var queue = new LlmRequestQueue(Host.Limits);
        var manager = Manager(queue);
        var main = manager.CurrentProvider;
        var fast = manager.FastProvider;

        manager.UpdateConfiguration(new AppSettingsData { BaseUrl = "https://beacon.example", LlmMaxConcurrentRequests = 3, LlmMonthlyBudget = 7m });

        manager.CurrentConfiguration.Should().BeEquivalentTo(Host, x => x.Excluding(y => y.Limits));
        manager.CurrentProvider.Should().NotBeNull().And.BeSameAs(main);
        manager.FastProvider.Should().NotBeNull().And.BeSameAs(fast);
        manager.InactiveReason.Should().BeNull();
        queue.AppliedLimits.MaxConcurrentRequests.Should().Be(3);
        manager.CurrentConfiguration.Limits.MonthlyBudget.Should().Be(7m);
    }

    [Test]
    public void SaveWithoutAProvider_ThatAppliesNoLimits_KeepsTheProviderAndTheLimits()
    {
        var queue = new LlmRequestQueue(Host.Limits);
        var manager = Manager(queue);
        var main = manager.CurrentProvider;

        manager.UpdateConfiguration(new AppSettingsData { LlmMaxConcurrentRequests = 3, LlmMonthlyBudget = 7m }, applyLimits: false);

        manager.CurrentConfiguration.Should().BeEquivalentTo(Host, x => x.Excluding(y => y.Limits.MonthlyBudget));
        manager.CurrentConfiguration.Limits.MonthlyBudget.Should().Be(7m);
        manager.CurrentProvider.Should().NotBeNull().And.BeSameAs(main);
        queue.AppliedLimits.MaxConcurrentRequests.Should().Be(10);
    }

    [Test]
    public void SaveWithoutAProvider_AfterAStoredOne_ReturnsToTheHostConfiguredProvider()
    {
        var manager = Manager(new LlmRequestQueue(Host.Limits));
        manager.UpdateConfiguration(new AppSettingsData { LlmProvider = AiProvider.Claude, LlmApiKey = "stored-key", LlmModel = "claude-stored" });
        manager.CurrentConfiguration.Provider.Should().Be(AiProvider.Claude);

        manager.UpdateConfiguration(new AppSettingsData { LlmMaxConcurrentRequests = 4 });

        manager.CurrentConfiguration.Should().BeEquivalentTo(Host, x => x.Excluding(y => y.Limits));
        manager.CurrentProvider.Should().NotBeNull();
        manager.CurrentConfiguration.Limits.MaxConcurrentRequests.Should().Be(4);
    }

    private static LlmProviderManager Manager(LlmRequestQueue queue)
    {
        LlmProviderManager? manager = null;
        manager = new LlmProviderManager(
            Host,
            TestLlmProviderFactory.Create(() => manager?.CurrentConfiguration ?? Host),
            queue,
            NullLogger<LlmProviderManager>.Instance);

        return manager;
    }
}
