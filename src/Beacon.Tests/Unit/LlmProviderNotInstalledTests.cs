using Beacon.AI.Handlers.AdminSettings;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A stored provider whose package is not installed never becomes the active provider: the manager starts or switches
/// to no provider with one warning, the previous provider never survives, and the connection test reports the install
/// message without building anything.
/// </summary>
[TestFixture]
public class LlmProviderNotInstalledTests
{
    private const string InstallMessage = "LLM provider Bedrock is not installed: reference Moberg.Beacon.AI.Bedrock and call AddBeaconBedrock().";
    private const string NotConfiguredMessage = "LLM provider is not configured. Configure it via Admin Settings.";

    [TestCase("")]
    [TestCase("legacyid:legacysecret")]
    public void Construction_WithAProviderThatIsNotInstalled_StartsWithNoProviderAndOneWarning(string apiKey)
    {
        var logger = new CapturingLogger<LlmProviderManager>();
        var config = BedrockConfig() with { ApiKey = apiKey };

        var manager = new LlmProviderManager(config, BuiltInsOnly(() => config), logger);

        manager.CurrentProvider.Should().BeNull();
        manager.InactiveReason.Should().Be(InstallMessage);
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("LLM provider Bedrock is not installed");
        logger.TextAtOrAbove(LogLevel.Trace).Should().NotContain("legacyid").And.NotContain("legacysecret");
    }

    [Test]
    public void Update_ToAProviderThatIsNotInstalled_DropsThePreviousProviderWithOneWarning()
    {
        LlmProviderManager? manager = null;
        var logger = new CapturingLogger<LlmProviderManager>();
        var start = new LlmConfiguration { Provider = AiProvider.OpenAI, ApiKey = TestCredentials.ProviderSecretKey, Model = "gpt-4o-mini" };
        var factory = BuiltInsOnly(() => manager?.CurrentConfiguration ?? start);
        manager = new LlmProviderManager(start, factory, logger);
        manager.CurrentProvider.Should().BeOfType<OpenAiProvider>();
        logger.Entries.Should().BeEmpty();

        manager.UpdateConfiguration(new AppSettingsData
        {
            LlmProvider = AiProvider.Bedrock,
            LlmRegion = "eu-west-1",
            LlmModel = "anthropic.claude-3-haiku",
            LlmApiKey = TestCredentials.ProviderSecretKey,
        });

        manager.CurrentConfiguration.Provider.Should().Be(AiProvider.Bedrock);
        manager.CurrentProvider.Should().BeNull("the previous provider could run under other credentials");
        manager.InactiveReason.Should().Be(InstallMessage);
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("LLM provider Bedrock is not installed");
        logger.TextAtOrAbove(LogLevel.Trace).Should().NotContain(TestCredentials.ProviderSecretKey);
    }

    [Test]
    public void SavesThatKeepAProviderThatIsNotInstalled_DoNotWarnAgain_ButSwitchingBackToItDoes()
    {
        var logger = new CapturingLogger<LlmProviderManager>();
        LlmProviderManager? manager = null;
        var start = BedrockConfig();
        manager = new LlmProviderManager(start, BuiltInsOnly(() => manager?.CurrentConfiguration ?? start), logger);
        var bedrock = new AppSettingsData { LlmProvider = AiProvider.Bedrock, LlmRegion = "eu-west-1", LlmModel = "anthropic.claude-3-haiku" };

        manager.UpdateConfiguration(bedrock);
        manager.UpdateConfiguration(new AppSettingsData { LlmProvider = AiProvider.Bedrock, LlmRegion = "eu-west-1", LlmModel = "anthropic.claude-3-sonnet" });

        logger.Entries.Should().ContainSingle("only the construction reports it; saves that keep the provider do not");
        manager.CurrentProvider.Should().BeNull();
        manager.InactiveReason.Should().Be(InstallMessage);

        manager.UpdateConfiguration(new AppSettingsData { LlmProvider = AiProvider.OpenAI, LlmModel = "gpt-4o-mini", LlmApiKey = TestCredentials.ProviderSecretKey });
        manager.UpdateConfiguration(bedrock);

        logger.Entries.Should().HaveCount(2).And.OnlyContain(x => x.Level == LogLevel.Warning);
        manager.CurrentProvider.Should().BeNull();
    }

    [Test]
    public void Update_FromAProviderThatIsNotInstalledToAnInstalledOne_ActivatesItAndClearsTheReason()
    {
        var logger = new CapturingLogger<LlmProviderManager>();
        LlmProviderManager? manager = null;
        var start = BedrockConfig();
        manager = new LlmProviderManager(start, BuiltInsOnly(() => manager?.CurrentConfiguration ?? start), logger);

        manager.UpdateConfiguration(new AppSettingsData { LlmProvider = AiProvider.OpenAI, LlmModel = "gpt-4o-mini", LlmApiKey = TestCredentials.ProviderSecretKey });

        manager.CurrentProvider.Should().BeOfType<OpenAiProvider>();
        manager.InactiveReason.Should().BeNull();
        logger.Entries.Should().ContainSingle("only the construction on the provider that is not installed warns");
    }

    [Test]
    public void Construction_WithAnInstalledProviderAndNoApiKeyYet_StartsWithNoProviderAndNoWarning()
    {
        var logger = new CapturingLogger<LlmProviderManager>();
        var config = new LlmConfiguration { Provider = AiProvider.OpenAI, Model = "gpt-4o-mini" };

        var manager = new LlmProviderManager(config, BuiltInsOnly(() => config), logger);

        manager.CurrentProvider.Should().BeNull();
        manager.InactiveReason.Should().BeNull("the provider is installed; it only waits for its key");
        logger.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task Calls_WithAProviderThatIsNotInstalled_FailWithTheInstallMessage()
    {
        var config = BedrockConfig();
        var manager = new LlmProviderManager(config, BuiltInsOnly(() => config), new CapturingLogger<LlmProviderManager>());
        var delegating = new DelegatingLlmProvider(manager, new LlmRequestQueue(1, 0));

        var complete = () => delegating.CompleteAsync(new LlmRequest { Messages = [new ChatMessage(ConversationRole.User, "hi")] });
        var count = () => delegating.CountTokensAsync("hi");

        await complete.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(InstallMessage);
        await count.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(InstallMessage);
    }

    [Test]
    public async Task Calls_WithAnInstalledProviderWithoutItsKey_FailAsNotConfigured()
    {
        var config = new LlmConfiguration { Provider = AiProvider.OpenAI, Model = "gpt-4o-mini" };
        var manager = new LlmProviderManager(config, BuiltInsOnly(() => config), new CapturingLogger<LlmProviderManager>());
        var delegating = new DelegatingLlmProvider(manager, new LlmRequestQueue(1, 0));

        var complete = () => delegating.CompleteAsync(new LlmRequest { Messages = [new ChatMessage(ConversationRole.User, "hi")] });
        var count = () => delegating.CountTokensAsync("hi");

        await complete.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(NotConfiguredMessage);
        await count.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(NotConfiguredMessage);
    }

    [Test]
    public void Construction_WithBedrockInstalledAndNoApiKey_ActivatesBedrock()
    {
        var logger = new CapturingLogger<LlmProviderManager>();
        var config = BedrockConfig();

        var manager = new LlmProviderManager(config, TestLlmProviderFactory.Create(() => config), logger);

        manager.CurrentProvider.Should().BeOfType<BedrockProvider>();
        logger.Entries.Should().BeEmpty();
    }

    [Test]
    public void Update_BetweenInstalledProviders_SwapsTheProvider()
    {
        LlmProviderManager? manager = null;
        var start = new LlmConfiguration { Provider = AiProvider.OpenAI, ApiKey = TestCredentials.ProviderSecretKey, Model = "gpt-4o-mini" };
        manager = new LlmProviderManager(
            start,
            TestLlmProviderFactory.Create(() => manager?.CurrentConfiguration ?? start),
            new CapturingLogger<LlmProviderManager>());

        manager.UpdateConfiguration(new AppSettingsData
        {
            LlmProvider = AiProvider.Claude,
            LlmModel = "claude-sonnet-4-20250514",
            LlmApiKey = TestCredentials.ProviderSecretKey,
        });

        manager.CurrentProvider.Should().BeOfType<ClaudeProvider>();
    }

    [Test]
    public async Task ConnectionTest_WithAProviderThatIsNotInstalled_FailsWithTheInstallMessage()
    {
        var tester = new LlmConnectionTester(new LlmRequestQueue(1, 0), BuiltInLlmProviderRegistrations.Create());

        var result = await tester.TestAsync(
            new LlmConnectionTestParameters(
                Provider: AiProvider.Bedrock,
                Model: "anthropic.claude-3-haiku",
                Region: "eu-west-1",
                BedrockAuthMode: BedrockAuthMode.IamRole,
                ApiKey: string.Empty,
                Endpoint: null,
                SessionToken: null,
                AwsAccessKeyId: null,
                AwsSecretAccessKey: null),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(InstallMessage);
        result.LatencyMs.Should().BeNull("nothing was sent to a provider");
    }

    private static LlmProviderFactory BuiltInsOnly(Func<LlmConfiguration> configAccessor)
    {
        return new LlmProviderFactory(configAccessor, BuiltInLlmProviderRegistrations.Create());
    }

    private static LlmConfiguration BedrockConfig()
    {
        return new LlmConfiguration { Provider = AiProvider.Bedrock, Region = "eu-west-1", Model = "anthropic.claude-3-haiku" };
    }
}
