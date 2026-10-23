using Beacon.AI;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Settings;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// A saved configuration hands its limits to the request queue, so a new concurrency limit and the rate limits apply
/// without a restart, while the queue-wait limits the host configured stay as they were.
/// </summary>
[TestFixture]
[CancelAfter(10_000)]
public class LlmProviderManagerLimitsTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Test]
    public void UpdateConfiguration_HandsTheSavedLimitsToTheQueue_KeepingTheHostsQueueWaits()
    {
        var start = new LlmConfiguration
        {
            Provider = AiProvider.OpenAI,
            Model = "gpt",
            Limits = new ProviderLimits { MaxConcurrentRequests = 8, InteractiveQueueWaitSeconds = 15 }
        };
        var queue = new LlmRequestQueue(start.Limits);
        var manager = Manager(start, queue);

        manager.UpdateConfiguration(new AppSettingsData
        {
            LlmProvider = AiProvider.OpenAI,
            LlmModel = "gpt",
            LlmMaxConcurrentRequests = 2,
            LlmRateLimitsEnabled = true,
            LlmRequestsPerMinute = 30,
            LlmTokensPerMinute = 9000
        });

        queue.AppliedLimits.Should().BeEquivalentTo(new
        {
            MaxConcurrentRequests = 2,
            RateLimitsEnabled = true,
            RequestsPerMinute = 30,
            TokensPerMinute = 9000,
            InteractiveQueueWaitSeconds = 15
        });
        manager.CurrentConfiguration.Limits.InteractiveQueueWaitSeconds.Should().Be(15);
    }

    [Test]
    public void TheManager_HandsTheHostsLimitsToTheQueue_WhenItIsBuilt()
    {
        var start = new LlmConfiguration { Limits = new ProviderLimits { MaxConcurrentRequests = 6, RateLimitsEnabled = true, RequestsPerMinute = 12 } };
        var queue = new LlmRequestQueue(new ProviderLimits { MaxConcurrentRequests = 50 });

        Manager(start, queue);

        queue.AppliedLimits.Should().BeSameAs(start.Limits);
    }

    [Test]
    public void AManagerWithoutAQueue_StillTakesTheSavedLimits()
    {
        var start = new LlmConfiguration { Provider = AiProvider.OpenAI, Model = "gpt" };
        LlmProviderManager? manager = null;
        manager = new LlmProviderManager(start, TestLlmProviderFactory.Create(() => manager?.CurrentConfiguration ?? start), NullLogger<LlmProviderManager>.Instance);

        manager.UpdateConfiguration(new AppSettingsData { LlmProvider = AiProvider.OpenAI, LlmMaxConcurrentRequests = 3 });
        manager.CurrentConfiguration.Limits.MaxConcurrentRequests.Should().Be(3);
        manager.UpdateLimits(new AppSettingsData { LlmMaxConcurrentRequests = 4 });

        manager.CurrentConfiguration.Limits.MaxConcurrentRequests.Should().Be(4);
    }

    [Test]
    public void TheSavedLimits_ApplyAlsoWhenNoProviderCanBeBuilt()
    {
        var start = new LlmConfiguration { Provider = AiProvider.OpenAI, Model = "gpt" };
        var queue = new LlmRequestQueue(start.Limits);
        var nothingInstalled = new LlmProviderFactory(() => start, []);
        var manager = new LlmProviderManager(start, nothingInstalled, queue, NullLogger<LlmProviderManager>.Instance);

        manager.UpdateConfiguration(new AppSettingsData { LlmProvider = AiProvider.Claude, LlmApiKey = "key", LlmMaxConcurrentRequests = 3 });

        manager.CurrentProvider.Should().BeNull();
        queue.AppliedLimits.MaxConcurrentRequests.Should().Be(3);
    }

    [Test]
    public void UpdateLimits_AppliesOnlyTheLimits_KeepingTheProviderAndItsCredentials()
    {
        var start = new LlmConfiguration { Provider = AiProvider.OpenAI, ApiKey = "host-key", Model = "gpt" };
        var queue = new LlmRequestQueue(start.Limits);
        var manager = Manager(start, queue);
        var provider = manager.CurrentProvider;

        manager.UpdateLimits(new AppSettingsData { LlmMaxConcurrentRequests = 5, LlmRateLimitsEnabled = true, LlmRequestsPerMinute = 40, LlmTokensPerMinute = 4000 });

        manager.CurrentConfiguration.Should().BeEquivalentTo(start, x => x.Excluding(y => y.Limits));
        manager.CurrentProvider.Should().NotBeNull().And.BeSameAs(provider);
        queue.AppliedLimits.Should().BeEquivalentTo(new { MaxConcurrentRequests = 5, RateLimitsEnabled = true, RequestsPerMinute = 40, TokensPerMinute = 4000 });
    }

    [Test]
    public void StoredLimitsThatDifferFromTheHosts_AreLoggedOnce_WithNumbersOnly()
    {
        var logs = new LogRecorder();
        var start = new LlmConfiguration { Provider = AiProvider.OpenAI, ApiKey = "host-key", Model = "gpt", Limits = new ProviderLimits { MaxConcurrentRequests = 10 } };
        LlmProviderManager? manager = null;
        manager = new LlmProviderManager(start, TestLlmProviderFactory.Create(() => manager?.CurrentConfiguration ?? start), null, logs.For<LlmProviderManager>());

        manager.UpdateLimits(new AppSettingsData { LlmMaxConcurrentRequests = 3 });
        manager.UpdateLimits(new AppSettingsData { LlmMaxConcurrentRequests = 4 });

        var entry = logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("Stored LLM limits replace the host-configured ones")).Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("3 concurrent requests (configured 10)").And.NotContain("host-key");
    }

    [Test]
    public void StoredLimitsEqualToTheHosts_AreNotReportedAsReplacingThem()
    {
        var logs = new LogRecorder();
        var start = new LlmConfiguration();
        LlmProviderManager? manager = null;
        manager = new LlmProviderManager(start, TestLlmProviderFactory.Create(() => manager?.CurrentConfiguration ?? start), null, logs.For<LlmProviderManager>());

        manager.UpdateLimits(new AppSettingsData());

        logs.Entries.Should().NotContain(x => x.Message.StartsWith("Stored LLM limits replace"));
    }

    [Test]
    public void TheContainer_BuildsTheManagerWithTheSharedQueue()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconAI(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<LlmProviderManager>();
        manager.UpdateLimits(new AppSettingsData { LlmMaxConcurrentRequests = 7 });

        provider.GetRequiredService<LlmRequestQueue>().AppliedLimits.MaxConcurrentRequests.Should().Be(7);
    }

    [Test]
    public async Task ASavedConcurrencyLimitOfTwo_AdmitsTwoRequests_AndHoldsTheThird()
    {
        var start = new LlmConfiguration { Provider = AiProvider.OpenAI, Model = "gpt" };
        var queue = new LlmRequestQueue(start.Limits);
        var manager = Manager(start, queue);
        manager.UpdateConfiguration(new AppSettingsData { LlmProvider = AiProvider.OpenAI, LlmMaxConcurrentRequests = 2 });
        var started = new[] { Started(), Started(), Started() };
        var provider = new Mock<ILlmProvider>();
        var calls = 0;
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started[Interlocked.Increment(ref calls) - 1].TrySetResult();
                return new TaskCompletionSource<LlmResponse>().Task;
            });

        for (var i = 0; i < 3; i++)
        {
            _ = queue.EnqueueRequestAsync(provider.Object, new LlmRequest());
        }

        await Task.WhenAll(started[0].Task, started[1].Task).WaitAsync(Guard);
        await Task.Delay(50);
        started[2].Task.IsCompleted.Should().BeFalse("two attempts run, which is the saved limit");
    }

    private static TaskCompletionSource Started()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static LlmProviderManager Manager(LlmConfiguration start, LlmRequestQueue queue)
    {
        LlmProviderManager? manager = null;
        manager = new LlmProviderManager(
            start,
            TestLlmProviderFactory.Create(() => manager?.CurrentConfiguration ?? start),
            queue,
            NullLogger<LlmProviderManager>.Instance);

        return manager;
    }
}
