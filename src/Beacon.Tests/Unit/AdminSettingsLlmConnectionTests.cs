using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AdminSettings;
using Beacon.Core.Models;
using Beacon.Core.Models.Settings;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// A SecurityAdmin changes the admin settings and tests the stored LLM connection, but the LLM provider, endpoint,
/// region, Bedrock authentication mode and credentials stay an Admin's: changing them, or testing with other values, is
/// refused (403) before anything is saved or dialled, so the stored credentials never follow an endpoint a SecurityAdmin
/// chose.
/// </summary>
[TestFixture]
public class AdminSettingsLlmConnectionTests
{
    private Mock<IAppSettingsService> _settings = null!;
    private Mock<ILlmConnectionTester> _tester = null!;
    private ILlmProviderCatalog _catalog = null!;

    [SetUp]
    public void SetUp()
    {
        _settings = new Mock<IAppSettingsService>();
        _settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettingsData
            {
                BaseUrl = "https://beacon.example",
                LlmProvider = AiProvider.AzureOpenAI,
                LlmModel = "gpt",
                LlmEndpoint = "https://llm.example",
                LlmApiKey = "stored-key",
                LlmRegion = "westeurope",
                LlmBedrockAuthMode = BedrockAuthMode.IamRole,
                LlmMaxConcurrentRequests = 4,
                LlmTokensPerMinute = 1000,
                LlmRequestsPerMinute = 60
            });
        _tester = new Mock<ILlmConnectionTester>();
        _tester
            .Setup(x => x.TestAsync(It.IsAny<LlmConnectionTestParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmConnectionTestResult(true, 5, "gpt", null));
        _catalog = Catalog(aiEnabled: true, AiProvider.OpenAI, AiProvider.Claude, AiProvider.AzureOpenAI, AiProvider.Bedrock);
    }

    [Test]
    public async Task ASecurityAdmin_ChangesTheOtherSettings_KeepingTheConnection()
    {
        await Update(SecurityAdmin()).Handle(Command(model: "gpt-next", budget: 50), CancellationToken.None);

        _settings.Verify(
            x => x.SaveSettingsAsync(
                It.Is<AppSettingsData>(y => y.LlmModel == "gpt-next" && y.LlmMonthlyBudget == 50 && y.LlmEndpoint == "https://llm.example" && y.LlmApiKey == "stored-key"),
                It.IsAny<string?>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestCase("provider")]
    [TestCase("endpoint")]
    [TestCase("apiKey")]
    [TestCase("region")]
    [TestCase("authMode")]
    [TestCase("sessionToken")]
    [TestCase("awsAccessKeyId")]
    [TestCase("awsSecret")]
    [TestCase("providerCleared")]
    [TestCase("regionCleared")]
    public async Task ASecurityAdmin_ChangingTheLlmConnection_IsForbidden_AndNothingIsSaved(string field)
    {
        var act = () => Update(SecurityAdmin()).Handle(Changing(field), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(LlmConnectionSettings.AdminOnlyMessage);
        _settings.Verify(x => x.SaveSettingsAsync(It.IsAny<AppSettingsData>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase("maxConcurrent")]
    [TestCase("requestsPerMinute")]
    [TestCase("tokensPerMinute")]
    [TestCase("enforced")]
    public async Task ASecurityAdmin_ChangingTheLlmLimits_IsForbidden_AndNothingIsSaved(string field)
    {
        var command = field switch
        {
            "maxConcurrent" => Command() with { LlmMaxConcurrentRequests = 40 },
            "requestsPerMinute" => Command() with { LlmRequestsPerMinute = 600 },
            "tokensPerMinute" => Command() with { LlmTokensPerMinute = 0 },
            _ => Command() with { LlmRateLimitsEnabled = true }
        };

        var act = () => Update(SecurityAdmin()).Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UpdateAdminSettingsHandler.LimitsAdminOnlyMessage);
        _settings.Verify(x => x.SaveSettingsAsync(It.IsAny<AppSettingsData>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ASecurityAdmin_SendingTheStoredLimitsBack_Saves()
    {
        await Update(SecurityAdmin()).Handle(Command(budget: 75) with { LlmRateLimitsEnabled = false }, CancellationToken.None);

        _settings.Verify(
            x => x.SaveSettingsAsync(
                It.Is<AppSettingsData>(y => y.LlmMonthlyBudget == 75 && y.LlmMaxConcurrentRequests == 4 && !y.LlmRateLimitsEnabled),
                It.IsAny<string?>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AnAdmin_SavesTheLlmLimits_AndWhetherTheyAreEnforced(bool enabled)
    {
        await Update(TestPermissions.Admin).Handle(
            Command() with { LlmMaxConcurrentRequests = 12, LlmRequestsPerMinute = 30, LlmRateLimitsEnabled = enabled },
            CancellationToken.None);

        _settings.Verify(
            x => x.SaveSettingsAsync(
                It.Is<AppSettingsData>(y => y.LlmRateLimitsEnabled == enabled && y.LlmMaxConcurrentRequests == 12 && y.LlmRequestsPerMinute == 30 && y.LlmTokensPerMinute == 1000),
                It.IsAny<string?>(),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ASaveWithoutTheEnforcementFlag_KeepsTheStoredOne()
    {
        _settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettingsData
            {
                BaseUrl = "https://beacon.example",
                LlmProvider = AiProvider.AzureOpenAI,
                LlmRegion = "westeurope",
                LlmMaxConcurrentRequests = 4,
                LlmTokensPerMinute = 1000,
                LlmRequestsPerMinute = 60,
                LlmRateLimitsEnabled = true
            });

        await Update(SecurityAdmin()).Handle(Command(model: "gpt-next"), CancellationToken.None);
        await Update(TestPermissions.Admin).Handle(Command(model: "gpt-latest"), CancellationToken.None);

        _settings.Verify(
            x => x.SaveSettingsAsync(It.Is<AppSettingsData>(y => y.LlmRateLimitsEnabled), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Test]
    public async Task AnAdmin_ChangesTheLlmConnection()
    {
        await Update(TestPermissions.Admin).Handle(Changing("endpoint") with { LlmApiKey = "new-key" }, CancellationToken.None);

        _settings.Verify(x => x.SaveSettingsAsync(It.Is<AppSettingsData>(y => y.LlmEndpoint == "https://elsewhere.example"), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase("https://phish.example")]
    [TestCase(null)]
    public async Task ASecurityAdmin_ChangingTheBaseUrl_IsForbidden_AndNothingIsSaved(string? baseUrl)
    {
        var act = () => Update(SecurityAdmin()).Handle(Command() with { BaseUrl = baseUrl }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UpdateAdminSettingsHandler.BaseUrlAdminOnlyMessage);
        _settings.Verify(x => x.SaveSettingsAsync(It.IsAny<AppSettingsData>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ASecurityAdmin_SendingTheStoredBaseUrlBack_ChangesNothing_AndAnAdminChangesIt()
    {
        await Update(SecurityAdmin()).Handle(Command() with { BaseUrl = " https://beacon.example " }, CancellationToken.None);
        await Update(TestPermissions.Admin).Handle(Command() with { BaseUrl = "https://beacon.example.org" }, CancellationToken.None);

        _settings.Verify(x => x.SaveSettingsAsync(It.Is<AppSettingsData>(y => y.BaseUrl == "https://beacon.example.org"), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _settings.Verify(x => x.SaveSettingsAsync(It.IsAny<AppSettingsData>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ASecurityAdmin_TestsTheStoredConnection_ButNotAnotherEndpointWithTheStoredKey()
    {
        var stored = await Test(SecurityAdmin()).Handle(
            new TestLlmConnectionCommand(null, "gpt", null, BedrockAuthMode.IamRole, null, null, null, null, null),
            CancellationToken.None);
        var elsewhere = () => Test(SecurityAdmin()).Handle(
            new TestLlmConnectionCommand(null, "gpt", null, BedrockAuthMode.IamRole, null, "https://other-endpoint.example", null, null, null),
            CancellationToken.None);

        stored.Ok.Should().BeTrue();
        await elsewhere.Should().ThrowAsync<BeaconForbiddenException>();
        _tester.Verify(x => x.TestAsync(It.Is<LlmConnectionTestParameters>(y => y.Endpoint == "https://other-endpoint.example"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task PickingAProviderThatIsNotInstalled_IsRefused_AndNothingIsSavedOrTested()
    {
        _catalog = Catalog(aiEnabled: true, AiProvider.OpenAI, AiProvider.AzureOpenAI);
        var expected = "LLM provider Bedrock is not installed: reference Moberg.Beacon.AI.Bedrock and call AddBeaconBedrock().";

        var update = () => Update(TestPermissions.Admin).Handle(Command() with { LlmProvider = AiProvider.Bedrock }, CancellationToken.None);
        var test = () => Test(TestPermissions.Admin).Handle(TestCommand(AiProvider.Bedrock), CancellationToken.None);

        await update.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(expected);
        await test.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(expected);
        _settings.Verify(x => x.SaveSettingsAsync(It.IsAny<AppSettingsData>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _tester.Verify(x => x.TestAsync(It.IsAny<LlmConnectionTestParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task KeepingAStoredProviderThatIsNotInstalled_IsAllowed()
    {
        // The stored provider is Azure OpenAI; this host installs only OpenAI.
        _catalog = Catalog(aiEnabled: true, AiProvider.OpenAI);

        await Update(SecurityAdmin()).Handle(Command(model: "gpt-next"), CancellationToken.None);
        await Update(TestPermissions.Admin).Handle(Command() with { LlmProvider = null }, CancellationToken.None);
        await Test(TestPermissions.Admin).Handle(TestCommand(null), CancellationToken.None);
        await Test(TestPermissions.Admin).Handle(TestCommand(AiProvider.AzureOpenAI), CancellationToken.None);

        _settings.Verify(x => x.SaveSettingsAsync(It.IsAny<AppSettingsData>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _tester.Verify(x => x.TestAsync(It.IsAny<LlmConnectionTestParameters>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task WhileAiIsOff_AnyProviderCanBePrepared()
    {
        _catalog = Catalog(aiEnabled: false);

        await Update(TestPermissions.Admin).Handle(Command() with { LlmProvider = AiProvider.Bedrock }, CancellationToken.None);
        await Test(TestPermissions.Admin).Handle(TestCommand(AiProvider.Bedrock), CancellationToken.None);

        _settings.Verify(x => x.SaveSettingsAsync(It.Is<AppSettingsData>(y => y.LlmProvider == AiProvider.Bedrock), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _tester.Verify(x => x.TestAsync(It.Is<LlmConnectionTestParameters>(y => y.Provider == AiProvider.Bedrock), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task PickingAnInstalledProvider_IsAllowed()
    {
        await Update(TestPermissions.Admin).Handle(Command() with { LlmProvider = AiProvider.Bedrock }, CancellationToken.None);
        await Test(TestPermissions.Admin).Handle(TestCommand(AiProvider.Claude) with { LlmApiKey = "claude-key" }, CancellationToken.None);

        _settings.Verify(x => x.SaveSettingsAsync(It.Is<AppSettingsData>(y => y.LlmProvider == AiProvider.Bedrock), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _tester.Verify(x => x.TestAsync(It.Is<LlmConnectionTestParameters>(y => y.Provider == AiProvider.Claude), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ASecurityAdmin_PickingAProviderThatIsNotInstalled_IsForbiddenFirst()
    {
        _catalog = Catalog(aiEnabled: true, AiProvider.OpenAI);

        var update = () => Update(SecurityAdmin()).Handle(Command() with { LlmProvider = AiProvider.Bedrock }, CancellationToken.None);
        var test = () => Test(SecurityAdmin()).Handle(TestCommand(AiProvider.Bedrock), CancellationToken.None);

        // A SecurityAdmin changing the provider is refused for that first (403), whether or not it is installed.
        await update.Should().ThrowAsync<BeaconForbiddenException>();
        await test.Should().ThrowAsync<BeaconForbiddenException>();
        _tester.Verify(x => x.TestAsync(It.IsAny<LlmConnectionTestParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task WithNoStoredProvider_PickingOneThatIsNotInstalledIsRefused_AndAnInstalledOneIsSaved()
    {
        _settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettingsData { BaseUrl = "https://beacon.example", LlmProvider = null });
        _catalog = Catalog(aiEnabled: true, AiProvider.OpenAI);

        var notInstalled = () => Update(TestPermissions.Admin).Handle(Command() with { LlmProvider = AiProvider.Bedrock }, CancellationToken.None);
        var notInstalledTest = () => Test(TestPermissions.Admin).Handle(TestCommand(AiProvider.Bedrock), CancellationToken.None);

        await notInstalled.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(LlmProviderInstallation.NotInstalledMessage(AiProvider.Bedrock));
        await notInstalledTest.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(LlmProviderInstallation.NotInstalledMessage(AiProvider.Bedrock));
        _settings.Verify(x => x.SaveSettingsAsync(It.IsAny<AppSettingsData>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        await Update(TestPermissions.Admin).Handle(Command() with { LlmProvider = AiProvider.OpenAI }, CancellationToken.None);

        _settings.Verify(x => x.SaveSettingsAsync(It.Is<AppSettingsData>(y => y.LlmProvider == AiProvider.OpenAI), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _tester.Verify(x => x.TestAsync(It.IsAny<LlmConnectionTestParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AnAdmin_ChangingTheEndpointWithoutTheKey_IsRefused_AndNothingIsSavedOrDialled()
    {
        var update = () => Update(TestPermissions.Admin).Handle(Command() with { LlmEndpoint = "https://elsewhere.example" }, CancellationToken.None);
        var test = () => Test(TestPermissions.Admin).Handle(TestCommand(AiProvider.AzureOpenAI) with { LlmEndpoint = "https://elsewhere.example" }, CancellationToken.None);

        await update.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(LlmConnectionSettings.NewDestinationNeedsCredentialMessage);
        await test.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(LlmConnectionSettings.NewDestinationNeedsCredentialMessage);
        _settings.Verify(x => x.SaveSettingsAsync(It.IsAny<AppSettingsData>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _tester.Verify(x => x.TestAsync(It.IsAny<LlmConnectionTestParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(BedrockAuthMode.AccessKey, null)]
    [TestCase(BedrockAuthMode.IamRole, "us-east-1")]
    public void ForBedrock_ChangingTheAuthModeOrRegionWithoutTheSecret_IsRefused(BedrockAuthMode mode, string? region)
    {
        var current = new AppSettingsData
        {
            LlmProvider = AiProvider.Bedrock,
            LlmRegion = "eu-west-1",
            LlmBedrockAuthMode = BedrockAuthMode.IamRole,
            LlmAwsSecretAccessKey = "stored-secret"
        };

        var refused = () => LlmConnectionSettings.DemandCredentialsForNewDestination(current, new LlmConnectionRequest(null, region, mode, null, null, null, null, null));
        var reentered = () => LlmConnectionSettings.DemandCredentialsForNewDestination(current, new LlmConnectionRequest(null, region, mode, null, null, null, null, "new-secret"));
        var unchanged = () => LlmConnectionSettings.DemandCredentialsForNewDestination(current, new LlmConnectionRequest(null, "eu-west-1", BedrockAuthMode.IamRole, null, null, null, null, null));

        refused.Should().Throw<InvalidOperationException>().WithMessage(LlmConnectionSettings.NewDestinationNeedsCredentialMessage);
        reentered.Should().NotThrow();
        unchanged.Should().NotThrow();
    }

    [Test]
    public async Task AnAdmin_ChangingTheProviderWithoutTheKey_IsRefused()
    {
        var act = () => Update(TestPermissions.Admin).Handle(Command() with { LlmProvider = AiProvider.OpenAI }, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(LlmConnectionSettings.NewDestinationNeedsCredentialMessage);
    }

    [Test]
    public async Task AnAdmin_ChangingTheEndpointWithTheKeyEnteredAgain_Saves()
    {
        await Update(TestPermissions.Admin).Handle(Command() with { LlmEndpoint = "https://elsewhere.example", LlmApiKey = "new-key" }, CancellationToken.None);
        await Test(TestPermissions.Admin).Handle(TestCommand(AiProvider.AzureOpenAI) with { LlmEndpoint = "https://elsewhere.example", LlmApiKey = "new-key" }, CancellationToken.None);

        _settings.Verify(
            x => x.SaveSettingsAsync(It.Is<AppSettingsData>(y => y.LlmEndpoint == "https://elsewhere.example" && y.LlmApiKey == "new-key"), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _tester.Verify(x => x.TestAsync(It.Is<LlmConnectionTestParameters>(y => y.ApiKey == "new-key"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AnAdmin_SendingTheStoredEndpointAgain_KeepsTheStoredKey()
    {
        await Update(TestPermissions.Admin).Handle(Command() with { LlmEndpoint = "https://llm.example" }, CancellationToken.None);

        _settings.Verify(
            x => x.SaveSettingsAsync(It.Is<AppSettingsData>(y => y.LlmApiKey == "stored-key"), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task AnAdmin_ChangingTheEndpoint_WithNoStoredKey_NeedsNoKey()
    {
        _settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettingsData { BaseUrl = "https://beacon.example", LlmProvider = AiProvider.AzureOpenAI, LlmEndpoint = "https://llm.example", LlmRegion = "westeurope" });

        await Update(TestPermissions.Admin).Handle(Command() with { LlmEndpoint = "https://elsewhere.example" }, CancellationToken.None);

        _settings.Verify(x => x.SaveSettingsAsync(It.IsAny<AppSettingsData>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase("http://beacon.example")]
    [TestCase("https://user:pw@beacon.example")]
    [TestCase("https://beacon.example/#x")]
    [TestCase("beacon.example")]
    public async Task ABaseUrlThatIsNotAnAbsoluteHttpsAddress_IsRefused_AndNothingIsSaved(string baseUrl)
    {
        var act = () => Update(TestPermissions.Admin).Handle(Command() with { BaseUrl = baseUrl }, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage(Beacon.Core.Helpers.BaseUrlPolicy.InvalidMessage);
        _settings.Verify(x => x.SaveSettingsAsync(It.IsAny<AppSettingsData>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AStoredBaseUrlThatPredatesTheCheck_DoesNotBlockOtherSettings()
    {
        _settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettingsData { BaseUrl = "http://old.example", LlmProvider = AiProvider.AzureOpenAI, LlmRegion = "westeurope", LlmEndpoint = "https://llm.example", LlmApiKey = "stored-key" });

        await Update(TestPermissions.Admin).Handle(Command(model: "gpt-next") with { BaseUrl = "http://old.example" }, CancellationToken.None);

        _settings.Verify(x => x.SaveSettingsAsync(It.Is<AppSettingsData>(y => y.LlmModel == "gpt-next"), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private UpdateAdminSettingsHandler Update(IBeaconPermissionAccessor permissions)
    {
        return new UpdateAdminSettingsHandler(
            _settings.Object,
            Mock.Of<IBeaconUserContext>(),
            TestProjectAccess.Actors(),
            permissions,
            _catalog,
            TestChangeApprovals.Llm(new ChangeGovernance.ChangeApprovalStore().Factory(), settingsService: _settings.Object),
            NullLogger<UpdateAdminSettingsHandler>.Instance);
    }

    private TestLlmConnectionHandler Test(IBeaconPermissionAccessor permissions)
    {
        return new TestLlmConnectionHandler(
            _settings.Object,
            _tester.Object,
            TestProjectAccess.Actors(),
            permissions,
            _catalog,
            NullLogger<TestLlmConnectionHandler>.Instance);
    }

    private static TestLlmConnectionCommand TestCommand(AiProvider? provider)
    {
        return new TestLlmConnectionCommand(provider, "gpt", null, BedrockAuthMode.IamRole, null, null, null, null, null);
    }

    private static ILlmProviderCatalog Catalog(bool aiEnabled, params AiProvider[] installed)
    {
        var catalog = new Mock<ILlmProviderCatalog>();
        catalog.SetupGet(x => x.AiEnabled).Returns(aiEnabled);
        catalog.SetupGet(x => x.Registered).Returns(installed.Select(x => new LlmProviderInfo(x, x != AiProvider.Bedrock)).ToList());
        return catalog.Object;
    }

    private static IBeaconPermissionAccessor SecurityAdmin() => TestPermissions.Holding(BeaconPermissions.SecurityAdmin);

    // What the settings page sends when only the named connection field changes.
    private static UpdateAdminSettingsCommand Changing(string field)
    {
        return Command() with
        {
            // Saving clears a provider or a region sent as null: unlike a test, an update does not keep the stored one.
            LlmProvider = field switch
            {
                "provider" => AiProvider.OpenAI,
                "providerCleared" => null,
                _ => AiProvider.AzureOpenAI
            },
            LlmEndpoint = field == "endpoint" ? "https://elsewhere.example" : null,
            LlmApiKey = field == "apiKey" ? "new-key" : null,
            LlmRegion = field switch
            {
                "region" => "eastus",
                "regionCleared" => null,
                _ => "westeurope"
            },
            LlmBedrockAuthMode = field == "authMode" ? BedrockAuthMode.AccessKey : BedrockAuthMode.IamRole,
            LlmSessionToken = field == "sessionToken" ? "token" : null,
            LlmAwsAccessKeyId = field == "awsAccessKeyId" ? "AKIAEXAMPLE" : null,
            LlmAwsSecretAccessKey = field == "awsSecret" ? "secret" : null
        };
    }

    private static UpdateAdminSettingsCommand Command(string model = "gpt", decimal budget = 10)
    {
        return new UpdateAdminSettingsCommand(
            "https://beacon.example",
            AiProvider.AzureOpenAI,
            model,
            null,
            "westeurope",
            BedrockAuthMode.IamRole,
            null,
            null,
            null,
            null,
            null,
            4,
            1000,
            60,
            budget);
    }
}
