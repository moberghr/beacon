using Beacon.AI;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AdminSettings;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The factory builds a provider only through the registration installed for it: the built-ins keep their fast-model
/// defaults, a provider that is not installed fails with the install message, and Bedrock comes from its own package.
/// </summary>
[TestFixture]
public class LlmProviderFactoryRegistrationTests
{
    private const string AzureEndpoint = "https://beacon-test.openai.azure.com/";

    [TestCase(AiProvider.OpenAI, typeof(OpenAiProvider))]
    [TestCase(AiProvider.Claude, typeof(ClaudeProvider))]
    [TestCase(AiProvider.AzureOpenAI, typeof(AzureOpenAiProvider))]
    [TestCase(AiProvider.Bedrock, typeof(BedrockProvider))]
    public void CreateProvider_DispatchesToTheRegistrationForTheConfiguredProvider(AiProvider provider, Type expected)
    {
        var config = Config(provider);

        var factory = TestLlmProviderFactory.Create(() => config);

        factory.CreateProvider().Should().BeOfType(expected);
        factory.CreateFastProvider().Should().BeOfType(expected);
    }

    [Test]
    public void CreateProvider_PassesTheConfigurationAndTheModelChoiceToTheRegistration()
    {
        var config = Config(AiProvider.Claude);
        var registration = new RecordingRegistration(AiProvider.Claude);
        var factory = new LlmProviderFactory(() => config, [registration]);

        factory.CreateProvider();
        factory.CreateFastProvider();

        registration.Calls.Should().Equal((config, false), (config, true));
    }

    [Test]
    public void CreateProvider_UsesTheMainModelForEveryBuiltIn()
    {
        Create(AiProvider.OpenAI, fast: false).Should().BeOfType<OpenAiProvider>().Which.Model.Should().Be("main-model");
        Create(AiProvider.Claude, fast: false).Should().BeOfType<ClaudeProvider>().Which.Model.Should().Be("main-model");
        Create(AiProvider.AzureOpenAI, fast: false).Should().BeOfType<AzureOpenAiProvider>().Which.Model.Should().Be("main-model");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void CreateFastProvider_WithoutAConfiguredFastModel_UsesEachBuiltInsDefault(string? fastModel)
    {
        Create(AiProvider.OpenAI, fast: true, fastModel).Should().BeOfType<OpenAiProvider>()
            .Which.Model.Should().Be("gpt-4o-mini");
        Create(AiProvider.Claude, fast: true, fastModel).Should().BeOfType<ClaudeProvider>()
            .Which.Model.Should().Be("claude-haiku-4-20250514");
        Create(AiProvider.AzureOpenAI, fast: true, fastModel).Should().BeOfType<AzureOpenAiProvider>()
            .Which.Model.Should().Be("main-model", "Azure falls back to the main deployment");
    }

    [Test]
    public void CreateFastProvider_AConfiguredFastModelWins()
    {
        Create(AiProvider.OpenAI, fast: true, "fast-model").Should().BeOfType<OpenAiProvider>().Which.Model.Should().Be("fast-model");
        Create(AiProvider.Claude, fast: true, "fast-model").Should().BeOfType<ClaudeProvider>().Which.Model.Should().Be("fast-model");
        Create(AiProvider.AzureOpenAI, fast: true, "fast-model").Should().BeOfType<AzureOpenAiProvider>().Which.Model.Should().Be("fast-model");
        Create(AiProvider.Bedrock, fast: true, "fast-model").Should().BeOfType<BedrockProvider>().Which.ModelId.Should().Be("fast-model");
    }

    [Test]
    public void CreateFastProvider_BedrockWithoutAFastModel_UsesTheMainModel()
    {
        Create(AiProvider.Bedrock, fast: true).Should().BeOfType<BedrockProvider>().Which.ModelId.Should().Be("main-model");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AzureOpenAI_WithoutAnEndpoint_Throws(bool fast)
    {
        var config = Config(AiProvider.AzureOpenAI) with { Endpoint = null };
        var factory = TestLlmProviderFactory.Create(() => config);

        var act = () => fast ? factory.CreateFastProvider() : factory.CreateProvider();

        act.Should().Throw<InvalidOperationException>().WithMessage("Azure OpenAI endpoint is required");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AProviderThatIsNotInstalled_FailsWithTheInstallMessage(bool fast)
    {
        var config = Config(AiProvider.Bedrock);
        var factory = new LlmProviderFactory(() => config, [new OpenAiProviderRegistration()]);

        var act = () => fast ? factory.CreateFastProvider() : factory.CreateProvider();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("LLM provider Bedrock is not installed: reference Moberg.Beacon.AI.Bedrock and call AddBeaconBedrock().");
    }

    [Test]
    public void RequiresApiKey_FollowsTheRegistration_AndIsTrueForAProviderThatIsNotInstalled()
    {
        var all = TestLlmProviderFactory.Create(() => Config(AiProvider.OpenAI));
        var builtInsOnly = new LlmProviderFactory(() => Config(AiProvider.OpenAI), BuiltInLlmProviderRegistrations.Create());

        all.RequiresApiKey(AiProvider.OpenAI).Should().BeTrue();
        all.RequiresApiKey(AiProvider.Claude).Should().BeTrue();
        all.RequiresApiKey(AiProvider.AzureOpenAI).Should().BeTrue();
        all.RequiresApiKey(AiProvider.Bedrock).Should().BeFalse();
        builtInsOnly.RequiresApiKey(AiProvider.Bedrock).Should().BeTrue("a provider that is not installed must never activate");
        builtInsOnly.IsRegistered(AiProvider.Bedrock).Should().BeFalse();
    }

    [Test]
    public void RegisteredProviders_AreListedInEnumOrder()
    {
        var factory = new LlmProviderFactory(
            () => Config(AiProvider.OpenAI),
            [new BedrockRegistration(), new AzureOpenAiProviderRegistration(), new OpenAiProviderRegistration()]);

        factory.RegisteredProviders.Should().Equal(AiProvider.OpenAI, AiProvider.AzureOpenAI, AiProvider.Bedrock);
    }

    [Test]
    public void TwoRegistrationsForOneProvider_TheOneRegisteredLastWins_WithAWarning()
    {
        var config = Config(AiProvider.Claude);
        var first = new RecordingRegistration(AiProvider.Claude);
        var last = new RecordingRegistration(AiProvider.Claude, requiresApiKey: false);
        var logger = new CapturingLogger<LlmProviderFactory>();

        var factory = new LlmProviderFactory(() => config, [first, last], logger);
        factory.CreateProvider();

        first.Calls.Should().BeEmpty();
        last.Calls.Should().ContainSingle();
        factory.RequiresApiKey(AiProvider.Claude).Should().BeFalse();
        factory.RegisteredProviders.Should().Equal(AiProvider.Claude);
        logger.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("Claude").And.Contain("registered last");
    }

#pragma warning disable CS0618 // the obsolete constructor is what these tests pin
    [TestCase(AiProvider.OpenAI, typeof(OpenAiProvider))]
    [TestCase(AiProvider.Claude, typeof(ClaudeProvider))]
    [TestCase(AiProvider.AzureOpenAI, typeof(AzureOpenAiProvider))]
    public void ObsoleteConstructor_BuildsTheBuiltInProviders(AiProvider provider, Type expected)
    {
        var config = Config(provider);

        new LlmProviderFactory(() => config).CreateProvider().Should().BeOfType(expected);
    }

    [Test]
    public void ObsoleteConstructor_CannotBuildBedrock()
    {
        var config = Config(AiProvider.Bedrock);
        var factory = new LlmProviderFactory(() => config);

        var act = () => factory.CreateProvider();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("LLM provider Bedrock is not installed: reference Moberg.Beacon.AI.Bedrock and call AddBeaconBedrock().");
        factory.RegisteredProviders.Should().Equal(AiProvider.OpenAI, AiProvider.Claude, AiProvider.AzureOpenAI);
    }
#pragma warning restore CS0618

    [Test]
    public void AddBeaconAI_Alone_RegistersTheBuiltInsButNotBedrock()
    {
        var services = Services();
        services.AddBeaconAI(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        provider.GetServices<ILlmProviderRegistration>().Select(x => x.Provider)
            .Should().BeEquivalentTo([AiProvider.OpenAI, AiProvider.Claude, AiProvider.AzureOpenAI]);
        provider.GetRequiredService<LlmProviderFactory>().IsRegistered(AiProvider.Bedrock).Should().BeFalse();
    }

    [Test]
    public void AddBeaconBedrock_RegistersBedrockOnce_BeforeOrAfterAddBeaconAI()
    {
        var before = Services();
        before.AddBeaconBedrock();
        before.AddBeaconAI(new ConfigurationBuilder().Build());
        before.AddBeaconBedrock();
        var after = Services();
        after.AddBeaconAI(new ConfigurationBuilder().Build());
        after.AddBeaconBedrock();
        after.AddBeaconBedrock();

        foreach (var services in new[] { before, after })
        {
            using var provider = services.BuildServiceProvider();

            provider.GetServices<ILlmProviderRegistration>().Should().ContainSingle(x => x.Provider == AiProvider.Bedrock)
                .Which.RequiresApiKey.Should().BeFalse();
            provider.GetServices<ILlmProviderRegistration>().Should().HaveCount(4);
            provider.GetRequiredService<LlmProviderFactory>().RegisteredProviders
                .Should().Equal(AiProvider.OpenAI, AiProvider.Claude, AiProvider.AzureOpenAI, AiProvider.Bedrock);
        }
    }

    [Test]
    public async Task WithAddBeaconBedrock_TheConnectionTesterFromDI_BuildsBedrock_AndReportsItsKeyError()
    {
        var services = Services();
        services.AddBeaconAI(new ConfigurationBuilder().Build());
        services.AddBeaconBedrock();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<ILlmConnectionTester>().TestAsync(
            new LlmConnectionTestParameters(
                Provider: AiProvider.Bedrock,
                Model: "anthropic.claude-3-haiku",
                Region: "eu-west-1",
                BedrockAuthMode: BedrockAuthMode.AccessKey,
                ApiKey: string.Empty,
                Endpoint: null,
                SessionToken: null,
                AwsAccessKeyId: null,
                AwsSecretAccessKey: null),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("Bedrock AccessKey authentication requires both an AWS access key id and a secret access key");
        result.LatencyMs.Should().BeNull("the provider could not be built, so nothing was sent");
    }

    [Test]
    public void WithAddBeaconBedrock_TheManagerFromDI_ActivatesBedrockWithoutAnApiKey()
    {
        var services = Services();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:LLM:Provider"] = nameof(AiProvider.Bedrock),
                ["Beacon:LLM:Region"] = "eu-west-1",
                ["Beacon:LLM:Model"] = "anthropic.claude-3-haiku",
            })
            .Build();
        services.AddBeaconAI(configuration);
        services.AddBeaconBedrock();
        using var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<LlmProviderManager>();

        manager.CurrentConfiguration.ApiKey.Should().BeEmpty();
        manager.CurrentProvider.Should().BeOfType<BedrockProvider>();
        manager.InactiveReason.Should().BeNull();
    }

    [Test]
    public void BeaconAI_ReferencesNoAwsAssembly_AndBedrockLivesInItsOwnAssembly()
    {
        typeof(LlmProviderFactory).Assembly.GetReferencedAssemblies()
            .Select(x => x.Name)
            .Should().NotContain(x => x != null && x.StartsWith("AWSSDK.", StringComparison.Ordinal));
        typeof(BedrockProvider).Assembly.GetName().Name.Should().Be("Beacon.AI.Bedrock");
    }

    [Test]
    public void TheInstallMessage_IsWordedTheSameInCoreAndInBeaconAI_ForEveryProvider()
    {
        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            LlmProviderInstallation.NotInstalledMessage(provider)
                .Should().Be(LlmProviderFactory.NotInstalledMessage(provider), "for {0}", provider);
        }
    }

    private static ILlmProvider Create(AiProvider provider, bool fast, string? fastModel = null)
    {
        var config = Config(provider) with { FastModel = fastModel };
        var factory = TestLlmProviderFactory.Create(() => config);

        return fast ? factory.CreateFastProvider() : factory.CreateProvider();
    }

    private static LlmConfiguration Config(AiProvider provider)
    {
        return new LlmConfiguration
        {
            Provider = provider,
            ApiKey = TestCredentials.ProviderSecretKey,
            Model = "main-model",
            Endpoint = AzureEndpoint,
            Region = "eu-west-1",
        };
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        return services;
    }

    private sealed class RecordingRegistration(AiProvider provider, bool requiresApiKey = true) : ILlmProviderRegistration
    {
        public List<(LlmConfiguration Configuration, bool Fast)> Calls { get; } = [];

        public AiProvider Provider => provider;

        public bool RequiresApiKey => requiresApiKey;

        public ILlmProvider Create(LlmConfiguration configuration, bool fast)
        {
            Calls.Add((configuration, fast));

            return new Mock<ILlmProvider>().Object;
        }
    }
}
