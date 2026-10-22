using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The base URL and LLM configuration an Admin stored take effect after a restart, not only when saved; what was not
/// stored, and settings that cannot be read, leave the host's configuration in force.
/// </summary>
[TestFixture]
public class StoredSettingsStartupTests
{
    private static readonly LlmConfiguration Bootstrap = new()
    {
        Provider = AiProvider.OpenAI,
        ApiKey = "bootstrap-key",
        Model = "bootstrap-model",
    };

    [Test]
    public async Task StoredLlmConfigurationAndBaseUrl_ReplaceTheHostConfiguration()
    {
        var manager = Manager();
        var configuration = new BeaconConfiguration { BaseUrl = "https://configured.example/beacon" };
        var applier = new StoredSettingsApplier(
            Settings(new AppSettingsData
            {
                BaseUrl = "https://stored.example/beacon",
                LlmProvider = AiProvider.Claude,
                LlmApiKey = "stored-key",
                LlmModel = "stored-model",
                LlmMaxConcurrentRequests = 7,
                LlmLimitsStored = true,
            }),
            configuration,
            NullLogger<StoredSettingsApplier>.Instance,
            manager);

        await applier.ApplyAsync(CancellationToken.None);

        configuration.BaseUrl.Should().Be("https://stored.example/beacon");
        manager.CurrentConfiguration.Provider.Should().Be(AiProvider.Claude);
        manager.CurrentConfiguration.ApiKey.Should().Be("stored-key");
        manager.CurrentConfiguration.Model.Should().Be("stored-model");
        manager.CurrentConfiguration.Limits.MaxConcurrentRequests.Should().Be(7);
    }

    [Test]
    public async Task AStoredProviderWithoutStoredLimits_KeepsTheHostLimits()
    {
        var hostLimits = new ProviderLimits { MaxConcurrentRequests = 1, RateLimitsEnabled = true, RequestsPerMinute = 10 };
        var bootstrap = Bootstrap with { Limits = hostLimits };
        var queue = new LlmRequestQueue(hostLimits);
        LlmProviderManager? manager = null;
        manager = new LlmProviderManager(
            bootstrap,
            TestLlmProviderFactory.Create(() => manager?.CurrentConfiguration ?? bootstrap),
            queue,
            NullLogger<LlmProviderManager>.Instance);
        var applier = new StoredSettingsApplier(
            Settings(new AppSettingsData { LlmProvider = AiProvider.Claude, LlmApiKey = "stored-key", LlmModel = "stored-model" }),
            new BeaconConfiguration(),
            NullLogger<StoredSettingsApplier>.Instance,
            manager);

        await applier.ApplyAsync(CancellationToken.None);

        manager.CurrentConfiguration.Model.Should().Be("stored-model");
        manager.CurrentConfiguration.Limits.Should().BeEquivalentTo(hostLimits, x => x.Excluding(y => y.MonthlyBudget));
        queue.AppliedLimits.Should().Be(hostLimits);
    }

    [Test]
    public async Task NothingStored_KeepsTheHostConfiguration()
    {
        var manager = Manager();
        var configuration = new BeaconConfiguration { BaseUrl = "https://configured.example/beacon" };
        var applier = new StoredSettingsApplier(
            Settings(new AppSettingsData()),
            configuration,
            NullLogger<StoredSettingsApplier>.Instance,
            manager);

        await applier.ApplyAsync(CancellationToken.None);

        configuration.BaseUrl.Should().Be("https://configured.example/beacon");
        manager.CurrentConfiguration.Should().Be(Bootstrap);
    }

    [Test]
    public async Task StoredLimitsWithoutAStoredProvider_ReplaceTheHostLimits_AndKeepTheHostProvider()
    {
        var logs = new LogRecorder();
        var queue = new LlmRequestQueue(Bootstrap.Limits);
        LlmProviderManager? manager = null;
        manager = new LlmProviderManager(
            Bootstrap,
            TestLlmProviderFactory.Create(() => manager?.CurrentConfiguration ?? Bootstrap),
            queue,
            logs.For<LlmProviderManager>());
        var applier = new StoredSettingsApplier(
            Settings(new AppSettingsData
            {
                LlmProvider = null,
                LlmMaxConcurrentRequests = 3,
                LlmRequestsPerMinute = 20,
                LlmTokensPerMinute = 5000,
                LlmRateLimitsEnabled = true,
                LlmLimitsStored = true,
            }),
            new BeaconConfiguration(),
            logs.For<StoredSettingsApplier>(),
            manager);

        await applier.ApplyAsync(CancellationToken.None);

        manager.CurrentConfiguration.Should().BeEquivalentTo(Bootstrap, x => x.Excluding(y => y.Limits));
        queue.AppliedLimits.Should().BeEquivalentTo(new { MaxConcurrentRequests = 3, RequestsPerMinute = 20, TokensPerMinute = 5000, RateLimitsEnabled = true });
        logs.Entries.Should().Contain(x => x.Level == LogLevel.Information && x.Message.StartsWith("Stored LLM limits replace the host-configured ones"));
        logs.Entries.Should().Contain(x => x.Message.Contains("LLM configuration False, LLM limits True"));
    }

    [Test]
    public async Task WithoutBeaconAi_OnlyTheBaseUrlApplies()
    {
        var configuration = new BeaconConfiguration();
        var applier = new StoredSettingsApplier(
            Settings(new AppSettingsData { BaseUrl = "https://stored.example/beacon", LlmProvider = AiProvider.Claude }),
            configuration,
            NullLogger<StoredSettingsApplier>.Instance);

        await applier.ApplyAsync(CancellationToken.None);

        configuration.BaseUrl.Should().Be("https://stored.example/beacon");
    }

    [Test]
    public async Task UnreadableSettings_LogAWarningWithoutDetails_AndKeepTheHostConfiguration()
    {
        var logs = new LogRecorder();
        var manager = Manager();
        var configuration = new BeaconConfiguration { BaseUrl = "https://configured.example/beacon" };
        var settings = new Mock<IAppSettingsService>();
        settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Host=db.internal;Password=secret"));
        var applier = new StoredSettingsApplier(settings.Object, configuration, logs.For<StoredSettingsApplier>(), manager);

        await applier.ApplyAsync(CancellationToken.None);

        configuration.BaseUrl.Should().Be("https://configured.example/beacon");
        manager.CurrentConfiguration.Should().Be(Bootstrap);
        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull();
        entry.FullText.Should().Contain(nameof(InvalidOperationException));
        entry.FullText.Should().NotContain("secret");
        entry.FullText.Should().NotContain("db.internal");
    }

    [Test]
    public async Task HostStart_AppliesTheStoredSettings()
    {
        var manager = Manager();
        var configuration = new BeaconConfiguration();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<ILlmConfigurationUpdater>(manager);
        builder.Services.AddSingleton(Settings(new AppSettingsData
        {
            BaseUrl = "https://stored.example/beacon",
            LlmProvider = AiProvider.Claude,
            LlmApiKey = "stored-key",
            LlmModel = "stored-model",
        }));
        builder.Services.AddTransient<StoredSettingsApplier>();
        builder.Services.AddTransient<IStartupFilter, StoredSettingsStartupApplier>();

        await using var app = builder.Build();
        await app.StartAsync();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (manager.CurrentConfiguration.Model != "stored-model" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        manager.CurrentConfiguration.Provider.Should().Be(AiProvider.Claude);
        manager.CurrentConfiguration.Model.Should().Be("stored-model");
        configuration.BaseUrl.Should().Be("https://stored.example/beacon");
    }

    [Test]
    public void AddBeaconApiAuthorization_RegistersTheStartupApplier()
    {
        var services = new ServiceCollection();

        services.AddBeaconApiAuthorization();

        services.Should().Contain(x => x.ServiceType == typeof(IStartupFilter) && x.ImplementationType == typeof(StoredSettingsStartupApplier));
    }

    private static IAppSettingsService Settings(AppSettingsData data)
    {
        var settings = new Mock<IAppSettingsService>();
        settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(data);

        return settings.Object;
    }

    private static LlmProviderManager Manager()
    {
        LlmProviderManager? manager = null;
        var factory = TestLlmProviderFactory.Create(() => manager?.CurrentConfiguration ?? Bootstrap);
        manager = new LlmProviderManager(Bootstrap, factory, NullLogger<LlmProviderManager>.Instance);

        return manager;
    }
}
