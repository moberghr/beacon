using Beacon.AI;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AdminSettings;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The admin settings list the LLM providers this host installed, in <see cref="AiProvider"/> order, with whether each
/// needs an API key. Beacon.AI's catalog replaces Core's AI-off default whichever registers first; without Beacon.AI
/// the catalog says AI is off and lists nothing.
/// </summary>
[TestFixture]
public class GetLlmProvidersHandlerTests
{
    [Test]
    public async Task WithAi_ListsTheInstalledProvidersInOrder()
    {
        var factory = new LlmProviderFactory(
            () => new LlmConfiguration(),
            [new BedrockRegistration(), .. BuiltInLlmProviderRegistrations.Create()]);

        var result = await new GetLlmProvidersHandler(new LlmProviderCatalog(factory))
            .Handle(new GetLlmProvidersQuery(), CancellationToken.None);

        result.AiEnabled.Should().BeTrue();
        result.Providers.Should().Equal(
            new LlmProviderInfo(AiProvider.OpenAI, true),
            new LlmProviderInfo(AiProvider.Claude, true),
            new LlmProviderInfo(AiProvider.AzureOpenAI, true),
            new LlmProviderInfo(AiProvider.Bedrock, false));
    }

    [Test]
    public async Task WithAiButWithoutBedrock_LeavesBedrockOut()
    {
        var factory = new LlmProviderFactory(() => new LlmConfiguration(), BuiltInLlmProviderRegistrations.Create());

        var result = await new GetLlmProvidersHandler(new LlmProviderCatalog(factory))
            .Handle(new GetLlmProvidersQuery(), CancellationToken.None);

        result.Providers.Select(x => x.Provider).Should().Equal(AiProvider.OpenAI, AiProvider.Claude, AiProvider.AzureOpenAI);
    }

    [Test]
    public async Task WithoutAi_SaysAiIsOff_AndListsNothing()
    {
        using var provider = CoreServices(useAi: false).BuildServiceProvider();

        var result = await new GetLlmProvidersHandler(provider.GetRequiredService<ILlmProviderCatalog>())
            .Handle(new GetLlmProvidersQuery(), CancellationToken.None);

        result.AiEnabled.Should().BeFalse();
        result.Providers.Should().BeEmpty();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TheAiCatalog_WinsOverCoresDefault_InEitherRegistrationOrder(bool coreFirst)
    {
        var services = coreFirst ? CoreServices(useAi: true) : new ServiceCollection();
        services.AddLogging();
        services.AddBeaconAI(new ConfigurationBuilder().Build());
        services.AddBeaconBedrock();
        if (!coreFirst)
        {
            AddCore(services, useAi: true);
        }

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ILlmProviderCatalog>();

        catalog.AiEnabled.Should().BeTrue();
        catalog.Registered.Select(x => x.Provider)
            .Should().Equal(AiProvider.OpenAI, AiProvider.Claude, AiProvider.AzureOpenAI, AiProvider.Bedrock);
    }

    private static ServiceCollection CoreServices(bool useAi)
    {
        var services = new ServiceCollection();
        AddCore(services, useAi);

        return services;
    }

    private static void AddCore(IServiceCollection services, bool useAi)
    {
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = TestEncryptionKey.Value
            })
            .Build();

        services.AddBeaconServices(configuration, x =>
        {
            x.UseAI = useAi;
            x.AddBeaconScheduler<NoOpScheduler>();
        });
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
