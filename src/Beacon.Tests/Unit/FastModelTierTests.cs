using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Models.Settings;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Short classification calls (intent, data-source routing) are tagged <see cref="LlmTier.Fast"/>; the manager builds the
/// configured fast model next to the main provider and the delegating provider sends a fast request to it, falling back
/// to the main provider whenever there is none.
/// </summary>
[TestFixture]
public class FastModelTierTests
{
    [Test]
    public void Manager_WithADifferentFastModel_BuildsAFastProvider()
    {
        var registration = new FakeRegistration();

        var manager = NewManager(registration, Config(fastModel: "small"));

        manager.CurrentProvider.Should().BeSameAs(registration.Main);
        manager.FastProvider.Should().BeSameAs(registration.Fast);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    [TestCase("big")]
    public void Manager_WithoutADistinctFastModel_BuildsNoFastProvider(string? fastModel)
    {
        var registration = new FakeRegistration();

        var manager = NewManager(registration, Config(fastModel));

        manager.CurrentProvider.Should().BeSameAs(registration.Main);
        manager.FastProvider.Should().BeNull();
        registration.FastBuilds.Should().Be(0);
    }

    [Test]
    public void UpdateConfiguration_RebuildsTheFastProvider_AndDropsItWhenTheFastModelIsCleared()
    {
        var registration = new FakeRegistration();
        var manager = NewManager(registration, Config(fastModel: "small"));
        var first = manager.FastProvider;

        manager.UpdateConfiguration(Settings(fastModel: "smaller"));
        var second = manager.FastProvider;
        manager.UpdateConfiguration(Settings(fastModel: null));

        first.Should().NotBeNull();
        second.Should().NotBeNull().And.NotBeSameAs(first);
        manager.FastProvider.Should().BeNull();
        manager.CurrentProvider.Should().NotBeNull();
    }

    [Test]
    public void Manager_WhenTheFastProviderCannotBeBuilt_KeepsTheMainProvider_AndLogsNoValues()
    {
        var registration = new FakeRegistration { FailFast = true };
        var logger = new CapturingLogger<LlmProviderManager>();

        var manager = NewManager(registration, Config(fastModel: "small"), logger);
        manager.UpdateConfiguration(Settings(fastModel: "small"));

        manager.CurrentProvider.Should().NotBeNull();
        manager.FastProvider.Should().BeNull();
        logger.Entries.Should().Contain(x => x.Level == LogLevel.Warning);
        logger.TextAtOrAbove(LogLevel.Trace).Should()
            .Contain("InvalidOperationException")
            .And.NotContain("boom")
            .And.NotContain("small")
            .And.NotContain(TestCredentials.ProviderSecretKey);
    }

    [Test]
    public async Task Delegating_RoutesFastToTheFastProvider_AndDefaultToTheMainProvider()
    {
        var registration = new FakeRegistration();
        var delegating = NewDelegating(registration, Config(fastModel: "small"));

        await delegating.CompleteAsync(new LlmRequest { Tier = LlmTier.Fast });
        await delegating.CompleteAsync(new LlmRequest());

        registration.Fast.Requests.Should().ContainSingle().Which.Tier.Should().Be(LlmTier.Fast);
        registration.Main.Requests.Should().ContainSingle().Which.Tier.Should().Be(LlmTier.Default);
    }

    [Test]
    public async Task Delegating_FastWithoutAFastProvider_UsesTheMainProvider()
    {
        var registration = new FakeRegistration();
        var delegating = NewDelegating(registration, Config(fastModel: null));

        var response = await delegating.CompleteAsync(new LlmRequest { Tier = LlmTier.Fast });

        response.Content.Should().Be("main");
        registration.Main.Requests.Should().ContainSingle();
    }

    [TestCase("BIG")]
    [TestCase("  big ")]
    public void Manager_WithAFastModelEqualToTheModelIgnoringCaseAndWhitespace_BuildsNoFastProvider(string fastModel)
    {
        var registration = new FakeRegistration();

        var manager = NewManager(registration, Config(fastModel));

        manager.FastProvider.Should().BeNull();
        registration.FastBuilds.Should().Be(0);
    }

    [Test]
    public void UpdateConfiguration_ToABlankApiKey_LeavesNeitherAMainNorAFastProvider()
    {
        var manager = NewManager(new FakeRegistration(), Config(fastModel: "small"));

        manager.UpdateConfiguration(Settings(fastModel: "small", apiKey: ""));

        manager.CurrentProvider.Should().BeNull();
        manager.FastProvider.Should().BeNull();
    }

    [Test]
    public void UpdateConfiguration_ToAnUnregisteredProvider_LeavesNeitherAMainNorAFastProvider()
    {
        var manager = NewManager(new FakeRegistration(), Config(fastModel: "small"));

        manager.UpdateConfiguration(Settings(fastModel: "small", provider: AiProvider.Claude));

        manager.CurrentProvider.Should().BeNull();
        manager.FastProvider.Should().BeNull();
    }

    [Test]
    public void UpdateConfiguration_WithAChangedApiKeyAndTheSameFastModel_BuildsANewFastProvider()
    {
        var manager = NewManager(new FakeRegistration(), Config(fastModel: "small"));
        var first = manager.FastProvider;

        manager.UpdateConfiguration(Settings(fastModel: "small", apiKey: TestCredentials.ProviderSecretKey + "2"));

        first.Should().NotBeNull();
        manager.FastProvider.Should().NotBeNull().And.NotBeSameAs(first);
    }

    [Test]
    public void ActiveProvidersSnapshot_ReturnsTheMainAndFastProviderOfOneConfiguration()
    {
        var registration = new FakeRegistration();
        var manager = NewManager(registration, Config(fastModel: "small"));
        var oldMain = registration.Main;
        var oldFast = registration.Fast;

        manager.UpdateConfiguration(Settings(fastModel: "smaller"));
        var (main, fast) = manager.ActiveProvidersSnapshot;

        main.Should().BeSameAs(registration.Main).And.NotBeSameAs(oldMain);
        fast.Should().BeSameAs(registration.Fast).And.NotBeSameAs(oldFast);
        main.Should().BeSameAs(manager.CurrentProvider);
        fast.Should().BeSameAs(manager.FastProvider);
    }

    [Test]
    public async Task Delegating_WhenTheFastProviderFails_ServesTheRequestOnTheMainProvider_AndLogsFailureCodesOnly()
    {
        var registration = new FakeRegistration { FastSetup = x => x.Failure = new InvalidOperationException("boom small") };
        var logger = new CapturingLogger<DelegatingLlmProvider>();
        var delegating = NewDelegating(registration, Config(fastModel: "small"), logger);

        var response = await delegating.CompleteAsync(new LlmRequest { Tier = LlmTier.Fast });

        response.Content.Should().Be("main");
        registration.Fast.Requests.Should().ContainSingle();
        registration.Main.Requests.Should().ContainSingle();
        logger.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning);
        logger.TextAtOrAbove(LogLevel.Trace).Should()
            .Contain("InvalidOperationException")
            .And.NotContain("boom")
            .And.NotContain("small");
    }

    [Test]
    public async Task Delegating_WhenTheCallerCancelsDuringTheFastCall_DoesNotRetryOnTheMainProvider()
    {
        using var cts = new CancellationTokenSource();
        var registration = new FakeRegistration
        {
            FastSetup = x =>
            {
                x.OnCall = () => cts.Cancel();
                x.CancelWhenTokenCancelled = true;
            }
        };
        var delegating = NewDelegating(registration, Config(fastModel: "small"));

        var act = () => delegating.CompleteAsync(new LlmRequest { Tier = LlmTier.Fast }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        registration.Main.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Delegating_SendsFastRequestsThroughTheQueue_AndItsConcurrencyLimitApplies()
    {
        var gate = new TaskCompletionSource();
        var registration = new FakeRegistration { FastSetup = x => x.Gate = gate.Task };
        var delegating = NewDelegating(registration, Config(fastModel: "small"));

        var first = delegating.CompleteAsync(new LlmRequest { Tier = LlmTier.Fast });
        var second = delegating.CompleteAsync(new LlmRequest { Tier = LlmTier.Fast });
        await Task.Delay(200);
        var startedWhileGated = registration.Fast.Requests.Count;
        gate.SetResult();
        await Task.WhenAll(first, second);

        startedWhileGated.Should().Be(1);
        registration.Fast.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task Delegating_TheFallbackRequestAlsoGoesThroughTheQueue()
    {
        var gate = new TaskCompletionSource();
        var registration = new FakeRegistration
        {
            FastSetup = x => x.Failure = new InvalidOperationException("x"),
            MainSetup = x => x.Gate = gate.Task
        };
        var delegating = NewDelegating(registration, Config(fastModel: "small"));

        var first = delegating.CompleteAsync(new LlmRequest { Tier = LlmTier.Fast });
        var second = delegating.CompleteAsync(new LlmRequest());
        await Task.Delay(200);
        var startedWhileGated = registration.Main.Requests.Count;
        gate.SetResult();
        await Task.WhenAll(first, second);

        startedWhileGated.Should().Be(1);
        registration.Main.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task IntentClassifier_SendsAFastTierRequest()
    {
        var llm = CapturingLlm("KNOWLEDGE");

        await new IntentClassifier().ClassifyAsync(llm.Object, "How do notifications work?", CancellationToken.None);

        llm.Invocations.Select(x => x.Arguments[0]).OfType<LlmRequest>()
            .Should().ContainSingle().Which.Tier.Should().Be(LlmTier.Fast);
    }

    [Test]
    public async Task DataSourceRouter_SendsAFastTierRequest()
    {
        var llm = CapturingLlm("""{ "sources": [] }""");
        var sources = new[] { 1, 2 }
            .Select(x => new Beacon.AI.Services.Knowledge.DataSourceKnowledge { DataSourceId = x, Name = $"Source {x}" })
            .ToList();

        await new DataSourceRouter(Microsoft.Extensions.Logging.Abstractions.NullLogger<DataSourceRouter>.Instance).RouteAsync(llm.Object, sources, "which orders shipped late?", CancellationToken.None);

        llm.Invocations.Select(x => x.Arguments[0]).OfType<LlmRequest>()
            .Should().ContainSingle().Which.Tier.Should().Be(LlmTier.Fast);
    }

    private static Mock<ILlmProvider> CapturingLlm(string content)
    {
        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = content });

        return llm;
    }

    private static LlmConfiguration Config(string? fastModel)
    {
        return new LlmConfiguration
        {
            Provider = AiProvider.OpenAI,
            ApiKey = TestCredentials.ProviderSecretKey,
            Model = "big",
            FastModel = fastModel,
        };
    }

    private static AppSettingsData Settings(string? fastModel, string? apiKey = TestCredentials.ProviderSecretKey, AiProvider provider = AiProvider.OpenAI)
    {
        return new AppSettingsData
        {
            LlmProvider = provider,
            LlmApiKey = apiKey,
            LlmModel = "big",
            LlmFastModel = fastModel,
        };
    }

    private static LlmProviderManager NewManager(FakeRegistration registration, LlmConfiguration config, ILogger<LlmProviderManager>? logger = null)
    {
        LlmProviderManager? manager = null;
        var factory = new LlmProviderFactory(() => manager?.CurrentConfiguration ?? config, [registration]);
        manager = new LlmProviderManager(config, factory, logger ?? new CapturingLogger<LlmProviderManager>());

        return manager;
    }

    private static DelegatingLlmProvider NewDelegating(FakeRegistration registration, LlmConfiguration config, ILogger<DelegatingLlmProvider>? logger = null)
    {
        return new DelegatingLlmProvider(NewManager(registration, config), new LlmRequestQueue(1, 1), logger);
    }

    private sealed class RecordingProvider(string name) : ILlmProvider
    {
        public List<LlmRequest> Requests { get; } = [];

        public Exception? Failure { get; set; }

        public Task? Gate { get; set; }

        public Action? OnCall { get; set; }

        public bool CancelWhenTokenCancelled { get; set; }

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            OnCall?.Invoke();
            if (CancelWhenTokenCancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Gate is not null)
            {
                await Gate;
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return new LlmResponse { Content = name };
        }

        public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TokenCount(1));
        }
    }

    private sealed class FakeRegistration : ILlmProviderRegistration
    {
        public AiProvider Provider => AiProvider.OpenAI;

        public bool RequiresApiKey => true;

        public bool FailFast { get; init; }

        public Action<RecordingProvider>? MainSetup { get; init; }

        public Action<RecordingProvider>? FastSetup { get; init; }

        public int FastBuilds { get; private set; }

        public RecordingProvider Main { get; private set; } = new("main");

        public RecordingProvider Fast { get; private set; } = new("fast");

        public ILlmProvider Create(LlmConfiguration configuration, bool fast)
        {
            if (!fast)
            {
                Main = new RecordingProvider("main");
                MainSetup?.Invoke(Main);

                return Main;
            }

            FastBuilds++;
            if (FailFast)
            {
                throw new InvalidOperationException("boom small");
            }

            Fast = new RecordingProvider("fast");
            FastSetup?.Invoke(Fast);

            return Fast;
        }
    }
}
