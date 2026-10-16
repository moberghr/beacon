using Amazon.Runtime;
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
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The Bedrock authentication mode an Admin chose decides which credentials the client is built with: access keys use
/// the stored pair (or, with both fields empty, the legacy <c>id:secret</c> API key), a missing half fails loudly, and only
/// the IAM mode reaches the default credential chain.
/// </summary>
[TestFixture]
public class BedrockAuthModeTests
{
    [Test]
    public void AccessKeyMode_BuildsTheClientWithTheConfiguredStaticKeys()
    {
        var provider = Create(Config(BedrockAuthMode.AccessKey));

        var credentials = provider.ExplicitCredentials.Should().BeOfType<BasicAWSCredentials>().Subject.GetCredentials();
        credentials.AccessKey.Should().Be(TestCredentials.AwsAccessKeyId);
        credentials.SecretKey.Should().Be(TestCredentials.AwsSecretAccessKey);
    }

    [TestCase(null, "secret")]
    [TestCase("", "secret")]
    [TestCase("id", null)]
    [TestCase("id", " ")]
    public void AccessKeyMode_WithAMissingHalf_ThrowsInsteadOfUsingTheDefaultChain(string? id, string? secret)
    {
        var config = Config(BedrockAuthMode.AccessKey) with { AwsAccessKeyId = id, AwsSecretAccessKey = secret };

        var act = () => new LlmProviderFactory(() => config).CreateProvider();

        act.Should().Throw<InvalidOperationException>().WithMessage("*access key id*secret access key*");
    }

    [Test]
    public void AccessKeyMode_TheAccessKeyFieldsWinOverTheLegacyApiKeyPair()
    {
        var provider = Create(Config(BedrockAuthMode.AccessKey) with { ApiKey = "legacyid:legacysecret" });

        var credentials = provider.ExplicitCredentials.Should().BeOfType<BasicAWSCredentials>().Subject.GetCredentials();
        credentials.AccessKey.Should().Be(TestCredentials.AwsAccessKeyId);
        credentials.SecretKey.Should().Be(TestCredentials.AwsSecretAccessKey);
    }

    [TestCase(null, null)]
    [TestCase("", " ")]
    public void AccessKeyMode_WithBothFieldsEmpty_UsesTheLegacyApiKeyPair(string? id, string? secret)
    {
        var provider = Create(Config(BedrockAuthMode.AccessKey) with
        {
            AwsAccessKeyId = id,
            AwsSecretAccessKey = secret,
            ApiKey = "legacyid:legacysecret",
        });

        var credentials = provider.ExplicitCredentials.Should().BeOfType<BasicAWSCredentials>().Subject.GetCredentials();
        credentials.AccessKey.Should().Be("legacyid");
        credentials.SecretKey.Should().Be("legacysecret");
    }

    [Test]
    public void TemporaryCredentialsMode_WithBothFieldsEmpty_UsesTheLegacyApiKeyPairAndTheSessionToken()
    {
        var provider = Create(Config(BedrockAuthMode.TemporaryCredentials) with
        {
            AwsAccessKeyId = null,
            AwsSecretAccessKey = null,
            ApiKey = "legacyid:legacysecret",
            SessionToken = TestCredentials.AwsSessionToken,
        });

        var credentials = provider.ExplicitCredentials.Should().BeOfType<SessionAWSCredentials>().Subject.GetCredentials();
        credentials.AccessKey.Should().Be("legacyid");
        credentials.Token.Should().Be(TestCredentials.AwsSessionToken);
    }

    [Test]
    public void AccessKeyMode_WithOneHalfOfTheFields_DoesNotFallBackToTheLegacyApiKeyPair()
    {
        var config = Config(BedrockAuthMode.AccessKey) with { AwsSecretAccessKey = null, ApiKey = "legacyid:legacysecret" };

        var act = () => new LlmProviderFactory(() => config).CreateProvider();

        act.Should().Throw<InvalidOperationException>().WithMessage("*access key id*secret access key*");
    }

    [TestCase("")]
    [TestCase("not-a-pair")]
    public void AccessKeyMode_WithNeitherTheFieldsNorALegacyPair_Throws(string apiKey)
    {
        var config = Config(BedrockAuthMode.AccessKey) with { AwsAccessKeyId = null, AwsSecretAccessKey = null, ApiKey = apiKey };

        var act = () => new LlmProviderFactory(() => config).CreateProvider();

        act.Should().Throw<InvalidOperationException>().WithMessage("*access key id*secret access key*");
    }

    [Test]
    public void TemporaryCredentialsMode_UsesTheKeysAndSessionToken()
    {
        var provider = Create(Config(BedrockAuthMode.TemporaryCredentials) with { SessionToken = TestCredentials.AwsSessionToken });

        var credentials = provider.ExplicitCredentials.Should().BeOfType<SessionAWSCredentials>().Subject.GetCredentials();
        credentials.AccessKey.Should().Be(TestCredentials.AwsAccessKeyId);
        credentials.Token.Should().Be(TestCredentials.AwsSessionToken);
    }

    [Test]
    public void TemporaryCredentialsMode_WithoutASessionToken_Throws()
    {
        var act = () => new LlmProviderFactory(() => Config(BedrockAuthMode.TemporaryCredentials)).CreateProvider();

        act.Should().Throw<InvalidOperationException>().WithMessage("*session token*");
    }

    [Test]
    public void IamRoleMode_UsesTheDefaultCredentialChain_EvenWhenKeysAreStored()
    {
        var logger = new CapturingLogger<LlmProviderFactory>();

        var provider = (BedrockProvider)new LlmProviderFactory(() => Config(BedrockAuthMode.IamRole), logger).CreateProvider();

        provider.ExplicitCredentials.Should().BeNull();
        logger.Entries.Should().BeEmpty();
    }

    [Test]
    public void IamRoleMode_StillHonoursTheLegacyApiKeyPair_AndWarnsWithoutKeyMaterial()
    {
        var logger = new CapturingLogger<LlmProviderFactory>();
        var config = Config(BedrockAuthMode.IamRole) with
        {
            AwsAccessKeyId = null,
            AwsSecretAccessKey = null,
            ApiKey = "legacyid:legacysecret",
        };

        var provider = (BedrockProvider)new LlmProviderFactory(() => config, logger).CreateProvider();

        var credentials = provider.ExplicitCredentials.Should().BeOfType<BasicAWSCredentials>().Subject.GetCredentials();
        credentials.AccessKey.Should().Be("legacyid");
        credentials.SecretKey.Should().Be("legacysecret");
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("legacy API key field").And.Contain("default AWS credential chain");
        logger.TextAtOrAbove(LogLevel.Trace).Should().NotContain("legacyid").And.NotContain("legacysecret");
    }

    [Test]
    public void UnknownMode_Throws()
    {
        var act = () => new LlmProviderFactory(() => Config((BedrockAuthMode)99)).CreateProvider();

        act.Should().Throw<InvalidOperationException>().WithMessage("*not supported*");
    }

    [Test]
    public void UpdateConfiguration_CarriesTheModeAndKeys_AndRebuildsTheProvider()
    {
        LlmProviderManager? manager = null;
        var factory = new LlmProviderFactory(() => manager!.CurrentConfiguration);
        manager = new LlmProviderManager(
            new LlmConfiguration { Provider = AiProvider.Bedrock, Region = "eu-west-1", Model = "anthropic.claude-3-haiku" },
            factory,
            NullLogger<LlmProviderManager>.Instance);

        manager.UpdateConfiguration(new AppSettingsData
        {
            LlmProvider = AiProvider.Bedrock,
            LlmRegion = "eu-west-1",
            LlmModel = "anthropic.claude-3-haiku",
            LlmBedrockAuthMode = BedrockAuthMode.AccessKey,
            LlmAwsAccessKeyId = TestCredentials.AwsAccessKeyId,
            LlmAwsSecretAccessKey = TestCredentials.AwsSecretAccessKey,
        });

        manager.CurrentConfiguration.BedrockAuthMode.Should().Be(BedrockAuthMode.AccessKey);
        manager.CurrentConfiguration.AwsAccessKeyId.Should().Be(TestCredentials.AwsAccessKeyId);
        manager.CurrentConfiguration.AwsSecretAccessKey.Should().Be(TestCredentials.AwsSecretAccessKey);
        manager.CurrentProvider.Should().BeOfType<BedrockProvider>()
            .Which.ExplicitCredentials.Should().BeOfType<BasicAWSCredentials>();
    }

    [Test]
    public void UpdateConfiguration_ThatCannotBuildAProvider_LeavesNoActiveProvider_NotThePreviousOne()
    {
        LlmProviderManager? manager = null;
        var factory = new LlmProviderFactory(() => manager!.CurrentConfiguration);
        manager = new LlmProviderManager(
            new LlmConfiguration { Provider = AiProvider.Bedrock, Region = "eu-west-1", Model = "anthropic.claude-3-haiku" },
            factory,
            NullLogger<LlmProviderManager>.Instance);
        manager.UpdateConfiguration(new AppSettingsData
        {
            LlmProvider = AiProvider.Bedrock,
            LlmRegion = "eu-west-1",
            LlmModel = "anthropic.claude-3-haiku",
            LlmBedrockAuthMode = BedrockAuthMode.IamRole,
        });
        manager.CurrentProvider.Should().BeOfType<BedrockProvider>()
            .Which.ExplicitCredentials.Should().BeNull("the starting provider uses the default credential chain");

        manager.UpdateConfiguration(new AppSettingsData
        {
            LlmProvider = AiProvider.Bedrock,
            LlmRegion = "eu-west-1",
            LlmModel = "anthropic.claude-3-haiku",
            LlmBedrockAuthMode = BedrockAuthMode.AccessKey,
            LlmAwsAccessKeyId = TestCredentials.AwsAccessKeyId,
            LlmAwsSecretAccessKey = null,
        });

        manager.CurrentConfiguration.BedrockAuthMode.Should().Be(BedrockAuthMode.AccessKey);
        manager.CurrentProvider.Should().BeNull();
    }

    [Test]
    public async Task AfterAFailedRebuild_TheDelegatingProviderRefusesWithTheNotConfiguredError()
    {
        LlmProviderManager? manager = null;
        var logger = new CapturingLogger<LlmProviderManager>();
        var factory = new LlmProviderFactory(() => manager!.CurrentConfiguration);
        manager = new LlmProviderManager(
            new LlmConfiguration { Provider = AiProvider.Bedrock, Region = "eu-west-1", Model = "anthropic.claude-3-haiku" },
            factory,
            logger);
        manager.UpdateConfiguration(new AppSettingsData
        {
            LlmProvider = AiProvider.Bedrock,
            LlmRegion = "eu-west-1",
            LlmModel = "anthropic.claude-3-haiku",
            LlmBedrockAuthMode = BedrockAuthMode.IamRole,
        });
        manager.CurrentProvider.Should().NotBeNull();
        logger.Entries.Clear();
        var delegating = new DelegatingLlmProvider(manager, new LlmRequestQueue(1, 0));

        manager.UpdateConfiguration(new AppSettingsData
        {
            LlmProvider = AiProvider.Bedrock,
            LlmRegion = "eu-west-1",
            LlmModel = "anthropic.claude-3-haiku",
            LlmBedrockAuthMode = BedrockAuthMode.AccessKey,
            LlmAwsAccessKeyId = TestCredentials.AwsAccessKeyId,
            LlmAwsSecretAccessKey = null,
        });

        var act = () => delegating.CompleteAsync(new LlmRequest { Messages = [new ChatMessage(ConversationRole.User, "hello")] });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("LLM provider is not configured*");
        var warning = logger.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Exception.Should().BeNull("only the failure's type and codes are logged, never an SDK message");
        warning.Message.Should().Contain(nameof(InvalidOperationException));
        logger.TextAtOrAbove(LogLevel.Trace).Should().NotContain(TestCredentials.AwsAccessKeyId);
    }

    [Test]
    public void UpdateConfiguration_NonBedrockProviderWithAnEmptyApiKey_LeavesNoActiveProvider()
    {
        LlmProviderManager? manager = null;
        var factory = new LlmProviderFactory(() => manager!.CurrentConfiguration);
        manager = new LlmProviderManager(
            new LlmConfiguration { Provider = AiProvider.OpenAI, Model = "gpt-4o-mini" },
            factory,
            NullLogger<LlmProviderManager>.Instance);
        manager.UpdateConfiguration(new AppSettingsData
        {
            LlmProvider = AiProvider.OpenAI,
            LlmModel = "gpt-4o-mini",
            LlmApiKey = TestCredentials.ProviderSecretKey,
        });
        manager.CurrentProvider.Should().BeOfType<OpenAiProvider>();

        manager.UpdateConfiguration(new AppSettingsData
        {
            LlmProvider = AiProvider.OpenAI,
            LlmModel = "gpt-4o-mini",
            LlmApiKey = string.Empty,
        });

        manager.CurrentProvider.Should().BeNull("a configuration without an API key cannot build a provider, and the previous key is never kept");
    }

    [Test]
    public async Task ConnectionTest_InAccessKeyMode_WithoutKeys_FailsWithoutReachingAws()
    {
        var tester = new LlmConnectionTester(new LlmRequestQueue(1, 0));

        var result = await tester.TestAsync(
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
        result.Error.Should().Contain("access key id");
    }

    private static BedrockProvider Create(LlmConfiguration config)
    {
        return (BedrockProvider)new LlmProviderFactory(() => config).CreateProvider();
    }

    private static LlmConfiguration Config(BedrockAuthMode mode)
    {
        return new LlmConfiguration
        {
            Provider = AiProvider.Bedrock,
            Region = "eu-west-1",
            Model = "anthropic.claude-3-haiku",
            BedrockAuthMode = mode,
            AwsAccessKeyId = TestCredentials.AwsAccessKeyId,
            AwsSecretAccessKey = TestCredentials.AwsSecretAccessKey,
        };
    }
}
