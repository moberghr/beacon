using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Security;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Services.Embed;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class DeploymentProfileTests
{
    private const string PublicBaseUrl = "https://beacon.example.test";

    // A throwaway value of the required length. Never a real secret (§1.2).
    private const string IntegrityKey = "test-only-integrity-key-placeholder-0001";

    [Test]
    public void NoProfile_LeavesEverySettingAtItsOwnDefault()
    {
        using var provider = Build([]);

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().NotThrow();
        var mcp = provider.GetRequiredService<IOptions<McpDeploymentOptions>>().Value;
        mcp.Audit.Required.Should().BeFalse();
        mcp.ForbidRowDataToLlm.Should().BeFalse();
        mcp.ForceReadOnly.Should().BeTrue();
        mcp.ForcePiiDetection.Should().BeTrue();
        mcp.Ceilings.MaxRowLimit.Should().BeNull();
        mcp.Ceilings.StatementTimeoutSeconds.Should().BeNull();
        mcp.Ceilings.MaxResultBytes.Should().BeNull();
        mcp.Ceilings.MaxConcurrentQueriesPerKey.Should().BeNull();
        provider.GetRequiredService<IOptions<RetentionOptions>>().Value.Locked.Should().BeFalse();
        var audit = provider.GetRequiredService<IOptions<SecurityAuditOptions>>().Value;
        audit.Required.Should().BeFalse();
        audit.ExecutionLog.Required.Should().BeFalse();
        provider.GetRequiredService<BeaconConfiguration>().UserManagement.MinimumPasswordLength.Should().Be(8);
        var profile = provider.GetRequiredService<IOptions<DeploymentProfileOptions>>().Value;
        profile.Profile.Should().Be(DeploymentProfile.None);
        profile.AppliedDefaults.Should().BeEmpty();
    }

    [Test]
    public void NoProfile_AcceptsWeakSettings()
    {
        using var provider = Build(
            new Dictionary<string, string?>
            {
                ["Beacon:Mcp:ForceReadOnly"] = "false",
                ["Beacon:RateLimits:Login:PermitLimit"] = "0",
                ["Beacon:Retention:Locked"] = "false",
                ["Beacon:Security:OriginGuard"] = "LogOnly",
                ["Beacon:Encryption:AllowLegacyCbc"] = "true"
            },
            weaken: x => x.ApprovalWorkflow.Enabled = false);

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().NotThrow();
    }

    [Test]
    public void Regulated_FillsTheSettingsTheHostLeftUnset()
    {
        using var provider = Build(Regulated());

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().NotThrow();
        var mcp = provider.GetRequiredService<IOptions<McpDeploymentOptions>>().Value;
        mcp.Audit.Required.Should().BeTrue();
        mcp.ForbidRowDataToLlm.Should().BeTrue();
        mcp.Ceilings.MaxRowLimit.Should().Be(1000);
        mcp.Ceilings.StatementTimeoutSeconds.Should().Be(30);
        mcp.Ceilings.MaxResultBytes.Should().Be(262144);
        mcp.Ceilings.MaxConcurrentQueriesPerKey.Should().Be(4);
        mcp.Ceilings.LearningSignalRetentionDays.Should().Be(90, "the regulated profile turns the retention lock on, which caps the learning signals at Retention:Days");
        provider.GetRequiredService<IOptions<RetentionOptions>>().Value.Locked.Should().BeTrue();
        var audit = provider.GetRequiredService<IOptions<SecurityAuditOptions>>().Value;
        audit.Required.Should().BeTrue();
        audit.ExecutionLog.Required.Should().BeTrue();
        provider.GetRequiredService<BeaconConfiguration>().UserManagement.MinimumPasswordLength.Should().Be(12);
        provider.GetRequiredService<IOptions<DeploymentProfileOptions>>().Value.AppliedDefaults.Should().Equal(
            "Beacon:Mcp:Audit:Required",
            "Beacon:Mcp:ForbidRowDataToLlm",
            "Beacon:Retention:Locked",
            "Beacon:Mcp:Ceilings:MaxRowLimit",
            "Beacon:Mcp:Ceilings:StatementTimeoutSeconds",
            "Beacon:Mcp:Ceilings:MaxResultBytes",
            "Beacon:Mcp:Ceilings:MaxConcurrentQueriesPerKey",
            "Beacon:Audit:Required",
            "Beacon:Audit:ExecutionLog:Required",
            "Beacon:Authentication:Cookie:SecureAlways",
            "Beacon:UserManagement:MinimumPasswordLength");
    }

    [Test]
    public void Regulated_KeepsTheValuesTheHostSet()
    {
        var settings = Regulated();
        settings["Beacon:Mcp:Ceilings:MaxRowLimit"] = "5000";
        settings["Beacon:Mcp:Audit:Required"] = "true";
        settings["Beacon:Audit:Required"] = "true";
        settings["Beacon:UserManagement:MinimumPasswordLength"] = "16";
        using var provider = Build(settings);

        provider.GetRequiredService<IStartupValidator>().Validate();

        provider.GetRequiredService<IOptions<McpDeploymentOptions>>().Value.Ceilings.MaxRowLimit.Should().Be(5000);
        provider.GetRequiredService<BeaconConfiguration>().UserManagement.MinimumPasswordLength.Should().Be(16);
        provider.GetRequiredService<IOptions<DeploymentProfileOptions>>().Value.AppliedDefaults.Should()
            .NotContain(["Beacon:Mcp:Ceilings:MaxRowLimit", "Beacon:Mcp:Audit:Required", "Beacon:Audit:Required", "Beacon:UserManagement:MinimumPasswordLength"]);
    }

    [TestCase("Beacon:Mcp:ForceReadOnly", "false", "Beacon:Mcp:ForceReadOnly")]
    [TestCase("Beacon:Mcp:ForcePiiDetection", "false", "Beacon:Mcp:ForcePiiDetection")]
    [TestCase("Beacon:Mcp:Audit:Required", "false", "Beacon:Mcp:Audit:Required")]
    [TestCase("Beacon:Mcp:ForbidRowDataToLlm", "false", "Beacon:Mcp:ForbidRowDataToLlm")]
    [TestCase("Beacon:Retention:Locked", "false", "Beacon:Retention:Locked")]
    [TestCase("Beacon:RateLimits:Login:PermitLimit", "0", "Beacon:RateLimits:Login:PermitLimit")]
    [TestCase("Beacon:RateLimits:Api:PermitLimit", "0", "Beacon:RateLimits:Api:PermitLimit")]
    [TestCase("Beacon:RateLimits:Anonymous:PermitLimit", "0", "Beacon:RateLimits:Anonymous:PermitLimit")]
    [TestCase("Beacon:RateLimits:Mcp:PermitLimit", "0", "Beacon:RateLimits:Mcp:PermitLimit")]
    [TestCase("Beacon:RateLimits:Ask:PermitLimit", "0", "Beacon:RateLimits:Ask:PermitLimit")]
    [TestCase("Beacon:RateLimits:Lockout:Enabled", "false", "Beacon:RateLimits:Lockout")]
    [TestCase("Beacon:UserManagement:LocalLogin:Enabled", "true", "Beacon:UserManagement:LocalLogin:Enabled")]
    [TestCase("Beacon:Authorization:RequireProjectMembership", "false", "Beacon:Authorization:RequireProjectMembership")]
    [TestCase("Beacon:UserManagement:MinimumPasswordLength", "10", "Beacon:UserManagement:MinimumPasswordLength")]
    [TestCase("Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys", "false", "Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys")]
    [TestCase("Beacon:Authentication:Cookie:SecureAlways", "false", "Beacon:Authentication:Cookie:SecureAlways")]
    [TestCase("Beacon:DataProtection:AllowUnencryptedKeys", "true", "Beacon:DataProtection:AllowUnencryptedKeys")]
    [TestCase("Beacon:Security:Csp", "ReportOnly", "Beacon:Security:Csp")]
    [TestCase("Beacon:Security:OriginGuard", "LogOnly", "Beacon:Security:OriginGuard")]
    [TestCase("Beacon:PublicBaseUrl", "http://beacon.example.test", "Beacon:PublicBaseUrl")]
    [TestCase("Beacon:Audit:Required", "false", "Beacon:Audit:Required")]
    [TestCase("Beacon:Audit:ExecutionLog:Required", "false", "Beacon:Audit:ExecutionLog:Required")]
    [TestCase("Beacon:Encryption:AllowLegacyCbc", "true", "Beacon:Encryption:AllowLegacyCbc")]
    [TestCase("Beacon:DataSources:Transport:EnforceOnExisting", "false", "Beacon:DataSources:Transport:EnforceOnExisting")]
    [TestCase("Beacon:DataSources:Transport:AllowInsecure", "true", "Beacon:DataSources:Transport:AllowInsecure")]
    [TestCase("Beacon:DataSources:AllowHostCredentials", "true", "Beacon:DataSources:AllowHostCredentials")]
    [TestCase("Beacon:DataSources:Outbound:AllowPrivateNetworks", "true", "Beacon:DataSources:AllowedHosts")]
    [TestCase("Beacon:LLM:AllowInsecureEndpoint", "true", "Beacon:LLM:AllowInsecureEndpoint")]
    public void Regulated_RefusesASettingTheHostSetWeaker(string key, string value, string expected)
    {
        var settings = Regulated();
        settings[key] = value;
        using var provider = Build(settings);

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().Throw<OptionsValidationException>().WithMessage($"*Beacon:Profile is Regulated, so {expected} must*");
    }

    [TestCase("Beacon:Security:OriginGuard", "Beacon:Security:OriginGuard")]
    [TestCase("Beacon:PublicBaseUrl", "Beacon:PublicBaseUrl")]
    [TestCase("Beacon:Audit:IntegrityKey", "Beacon:Audit:IntegrityKey")]
    [TestCase("Beacon:Encryption:AllowLegacyCbc", "Beacon:Encryption:AllowLegacyCbc")]
    [TestCase("Beacon:DataSources:Transport:EnforceOnExisting", "Beacon:DataSources:Transport:EnforceOnExisting")]
    [TestCase("Beacon:Authorization:RequireProjectMembership", "Beacon:Authorization:RequireProjectMembership")]
    [TestCase("Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys", "Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys")]
    [TestCase("Beacon:DataSources:Outbound:AllowPrivateNetworks", "Beacon:DataSources:AllowedHosts")]
    public void Regulated_RefusesARequiredSettingTheHostLeftUnset(string key, string expected)
    {
        var settings = Regulated();
        settings.Remove(key);
        using var provider = Build(settings);

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().Throw<OptionsValidationException>().WithMessage($"*Beacon:Profile is Regulated, so {expected} must*");
    }

    [Test]
    public void Regulated_AcceptsAHostListInPlaceOfClosedPrivateNetworks()
    {
        var settings = Regulated();
        settings.Remove("Beacon:DataSources:Outbound:AllowPrivateNetworks");
        settings["Beacon:DataSources:AllowedHosts:0"] = "db.example.test";
        using var provider = Build(settings);

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().NotThrow();
    }

    [TestCaseSource(nameof(WeakCodeOptions))]
    public void Regulated_RefusesACodeOptionTheHostSetWeaker(string expected, Action<BeaconConfiguration> weaken)
    {
        using var provider = Build(Regulated(), weaken: weaken);

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().Throw<OptionsValidationException>().WithMessage($"*Beacon:Profile is Regulated, so {expected} must*(in code*");
    }

    [Test]
    public void Regulated_ListsEveryRefusedSettingInOneFailure()
    {
        var settings = Regulated();
        settings["Beacon:Mcp:ForceReadOnly"] = "false";
        settings["Beacon:Mcp:ForbidRowDataToLlm"] = "false";
        settings["Beacon:RateLimits:Mcp:PermitLimit"] = "0";
        settings["Beacon:Security:OriginGuard"] = "LogOnly";
        using var provider = Build(settings, weaken: x => x.ApprovalWorkflow.CoversAdminRoleGrants = false);

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        var failures = startup.Should().Throw<OptionsValidationException>().Which.Failures;
        failures.Should().Contain(x => x.Contains("Beacon:Mcp:ForceReadOnly must be true"));
        failures.Should().Contain(x => x.Contains("Beacon:Mcp:ForbidRowDataToLlm must be true"));
        failures.Should().Contain(x => x.Contains("Beacon:RateLimits:Mcp:PermitLimit must be greater than zero"));
        failures.Should().Contain(x => x.Contains("Beacon:Security:OriginGuard must be Enforce"));
        failures.Should().Contain(x => x.Contains("ApprovalWorkflow.CoversAdminRoleGrants must be true (in code)"));
    }

    [Test]
    public void Regulated_NeverNamesAConfiguredValue()
    {
        var settings = Regulated();
        settings["Beacon:PublicBaseUrl"] = "http://beacon-host.example.test";
        settings["Beacon:Security:OriginGuard"] = "LogOnly";
        using var provider = Build(settings);

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        var message = startup.Should().Throw<OptionsValidationException>().Which.Message;
        message.Should().Contain("Beacon:PublicBaseUrl must be set");
        message.Should().NotContain("beacon-host.example.test").And.NotContain(IntegrityKey).And.NotContain("LogOnly");
    }

    [Test]
    public void Regulated_RequiresEveryRetentionWindowThroughTheLock()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Beacon:Profile"] = "Regulated",
            ["Beacon:UserManagement:LocalLogin:Enabled"] = "false"
        });

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().Throw<OptionsValidationException>()
            .WithMessage("*Beacon:Retention:Locked is on, so Beacon:Retention:Days must be greater than zero*");
    }

    [Test]
    public void Regulated_RequiresMultiFactorAuthenticationWhileSingleSignOnIsOn()
    {
        var settings = Regulated();
        settings["Beacon:Authentication:Oidc:Enabled"] = "true";
        using var refused = Build(settings, withOidc: true);

        var startup = () => refused.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().Throw<OptionsValidationException>()
            .WithMessage("*Beacon:Profile is Regulated, so Beacon:Authentication:Oidc:RequireMfa must be true*");

        settings["Beacon:Authentication:Oidc:RequireMfa"] = "true";
        using var admitted = Build(settings, withOidc: true);

        var admittedStartup = () => admitted.GetRequiredService<IStartupValidator>().Validate();

        admittedStartup.Should().NotThrow();
    }

    [Test]
    public void Assess_FindsNothingInACompliantDeployment()
    {
        RegulatedProfile.Assess(Compliant()).Should().BeEmpty();
    }

    [Test]
    public void Assess_IgnoresMultiFactorAuthenticationWhileSingleSignOnIsOff()
    {
        var findings = RegulatedProfile.Assess(Compliant(oidc: new OidcAuthenticationOptions { Enabled = false, RequireMfa = false }));

        findings.Should().BeEmpty();
    }

    [TestCase("Beacon:PublicBaseUrl", "https://beacon.example.test/", true)]
    [TestCase("Beacon:PublicBaseUrl", "http://beacon.example.test", false)]
    [TestCase("Beacon:PublicBaseUrl", "beacon.example.test", false)]
    [TestCase("Beacon:PublicBaseUrl", "/beacon", false)]
    [TestCase("Beacon:Security:OriginGuard", " enforce ", true)]
    [TestCase("Beacon:Security:OriginGuard", "1", false)]
    [TestCase("Beacon:Security:Csp", "Enforce", true)]
    [TestCase("Beacon:Security:Csp", "Off", false)]
    [TestCase("Beacon:Encryption:AllowLegacyCbc", "False", true)]
    [TestCase("Beacon:Encryption:AllowLegacyCbc", "no", false)]
    [TestCase("Beacon:Authentication:Cookie:SecureAlways", "True", true)]
    [TestCase("Beacon:DataProtection:AllowUnencryptedKeys", "false", true)]
    public void Assess_JudgesTheKeysReadFromTheHostsConfiguration(string key, string value, bool holds)
    {
        var findings = RegulatedProfile.Assess(Compliant(new Dictionary<string, string?> { [key] = value }));

        if (holds)
        {
            findings.Should().BeEmpty();
            return;
        }

        findings.Should().ContainSingle().Which.Setting.Should().Be(key);
    }

    [Test]
    public void Assess_ObsoleteOverload_JudgesBeaconsDefaultsForTheSettingsItIsNotGiven()
    {
#pragma warning disable CS0618 // The obsolete overload stays for hosts that call it.
        var findings = RegulatedProfile.Assess(
            Strong(),
            new RetentionOptions { Locked = true },
            new RateLimitOptions(),
            null,
            new LocalLoginOptions { Enabled = false });
#pragma warning restore CS0618

        var settings = findings.Select(x => x.Setting).ToList();
        settings.Should().Contain("UserManagement.Enabled").And.Contain("Beacon:Security:OriginGuard");
        settings.Should().NotContain("Beacon:Mcp:ForbidRowDataToLlm").And.NotContain("Beacon:UserManagement:LocalLogin:Enabled");
    }

    [Test]
    public void ApiKeys_AreTheKeysTheProfileJudges()
    {
        BeaconCookieSecurity.SecureAlwaysKey.Should().Be(RegulatedProfile.CookieSecureAlwaysKey);
        BeaconDataProtectionExtensions.AllowUnencryptedKeysKey.Should().Be(RegulatedProfile.AllowUnencryptedKeysKey);
        $"{BeaconHostSecurityOptions.SectionName}:{nameof(BeaconHostSecurityOptions.Csp)}".Should().Be(RegulatedProfile.CspKey);
        $"{BeaconHostSecurityOptions.SectionName}:{nameof(BeaconHostSecurityOptions.OriginGuard)}".Should().Be(RegulatedProfile.OriginGuardKey);
        nameof(BeaconCspMode.Enforce).Should().Be("Enforce");
        nameof(BeaconOriginGuardMode.Enforce).Should().Be("Enforce");
    }

    [TestCase("regulated", DeploymentProfile.Regulated)]
    [TestCase(" REGULATED ", DeploymentProfile.Regulated)]
    [TestCase("none", DeploymentProfile.None)]
    [TestCase("", DeploymentProfile.None)]
    public void Profile_IsReadByNameCaseInsensitively(string value, DeploymentProfile expected)
    {
        var options = DeploymentProfileOptions.From(Configuration(new() { ["Beacon:Profile"] = value }));

        options.Profile.Should().Be(expected);
        options.UnknownValue.Should().BeNull();
    }

    [TestCase("Strict")]
    [TestCase("1")]
    public void UnknownProfile_FailsStartup(string value)
    {
        using var provider = Build(new Dictionary<string, string?> { ["Beacon:Profile"] = value });

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().Throw<OptionsValidationException>().WithMessage("*Beacon:Profile names no profile; known profiles: None, Regulated*");
    }

    [Test]
    public void Report_WithoutAProfile_WarnsOnceNamingEachWeakSetting()
    {
        var recorder = new LogRecorder();
        var settings = new RegulatedProfileSettings();
        var expectedCount = RegulatedProfile.Assess(settings).Count;

        Report(recorder, new DeploymentProfileOptions()).Report(settings);

        var entry = recorder.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.EventId.Should().Be(DeploymentProfileStartupReport.WeakSettingsEvent);
        entry.Message.Should().Contain($"{expectedCount} security settings are weaker than the regulated profile requires (profile: None)")
            .And.Contain("Beacon:Mcp:Audit:Required must be true")
            .And.Contain("Beacon:Mcp:Ceilings:MaxRowLimit must be set")
            .And.Contain("Beacon:Retention:Locked must be true")
            .And.Contain("Beacon:UserManagement:LocalLogin:Enabled must be false")
            .And.Contain("UserManagement.Enabled must be true (in code")
            .And.Contain("ApprovalWorkflow.Enabled must be true (in code")
            .And.Contain("Beacon:Security:OriginGuard must be Enforce")
            .And.Contain("Beacon:Audit:IntegrityKey must be set")
            .And.Contain("Beacon:DataSources:AllowedHosts must list")
            .And.Contain("Set Beacon:Profile to Regulated");
        entry.Message.Should().NotContain("Beacon:Mcp:ForceReadOnly", "a lock that is on is not weak")
            .And.NotContain("Beacon:Security:Csp", "the Content-Security-Policy is enforced by default");
    }

    [Test]
    public void Report_WithoutAProfile_LogsNothingWhenEverySettingHolds()
    {
        var recorder = new LogRecorder();

        Report(recorder, new DeploymentProfileOptions()).Report(Compliant(embed: true, migrations: true));

        recorder.Entries.Should().BeEmpty();
    }

    [Test]
    public void Report_UnderTheRegulatedProfile_NamesTheSettingsItFilled()
    {
        var recorder = new LogRecorder();
        var profile = new DeploymentProfileOptions
        {
            Profile = DeploymentProfile.Regulated,
            AppliedDefaults = ["Beacon:Mcp:Audit:Required", "Beacon:Retention:Locked"]
        };

        Report(recorder, profile).Report(Compliant());

        var entry = recorder.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Information);
        entry.EventId.Should().Be(DeploymentProfileStartupReport.ProfileHoldsEvent);
        entry.Message.Should().Contain("Beacon:Profile is Regulated and every setting it requires holds")
            .And.Contain("Beacon:Mcp:Audit:Required, Beacon:Retention:Locked");
    }

    [Test]
    public void Report_UnderTheRegulatedProfile_NamesTheSurfacesThatAreOn()
    {
        var recorder = new LogRecorder();
        var profile = new DeploymentProfileOptions { Profile = DeploymentProfile.Regulated };

        Report(recorder, profile).Report(Compliant(embed: true, migrations: true));

        recorder.Entries.Should().HaveCount(2);
        var entry = recorder.Entries[1];
        entry.Level.Should().Be(LogLevel.Information);
        entry.EventId.Should().Be(DeploymentProfileStartupReport.SurfacesOnEvent);
        entry.Message.Should().Contain("Beacon:Embed:Enabled, Beacon:Migrations:Enabled");
    }

    [Test]
    public void Report_ReadsTheSettingsTheHostResolved()
    {
        using var provider = Build(Regulated());
        var recorder = new LogRecorder();
        var report = new DeploymentProfileStartupReport(
            recorder.For<DeploymentProfileStartupReport>(),
            provider.GetRequiredService<IOptions<DeploymentProfileOptions>>(),
            provider);

        report.Configure(_ => { })(null!);

        var entry = recorder.Entries.Should().ContainSingle().Which;
        entry.EventId.Should().Be(DeploymentProfileStartupReport.ProfileHoldsEvent);
        entry.Message.Should().Contain("Beacon:Audit:Required").And.Contain("Beacon:UserManagement:MinimumPasswordLength");
    }

    [Test]
    public void Report_NeverCarriesAConfiguredValue()
    {
        var recorder = new LogRecorder();
        var beacon = new BeaconConfiguration();
        beacon.UserManagement.LocalLogin = new LocalLoginOptions { Enabled = true, BreakGlassUserNames = ["break-glass-name"] };
        var settings = new RegulatedProfileSettings
        {
            Oidc = new OidcAuthenticationOptions { Enabled = true, ClientSecret = "client-secret-value", Authority = "https://login.example.test/tenant" },
            Beacon = beacon,
            Audit = new SecurityAuditOptions { IntegrityKey = IntegrityKey },
            Configuration = Configuration(new() { ["Beacon:PublicBaseUrl"] = "http://beacon-host.example.test" })
        };

        Report(recorder, new DeploymentProfileOptions()).Report(settings);

        var text = recorder.Entries.Should().ContainSingle().Which.FullText;
        text.Should().Contain("Beacon:Authentication:Oidc:RequireMfa must be true").And.Contain("Beacon:PublicBaseUrl must be set");
        text.Should().NotContain("client-secret-value")
            .And.NotContain("login.example.test")
            .And.NotContain("break-glass-name")
            .And.NotContain("beacon-host.example.test")
            .And.NotContain(IntegrityKey);
    }

    private static IEnumerable<TestCaseData> WeakCodeOptions()
    {
        yield return Weak("UserManagement.Enabled", x => x.UserManagement.Enabled = false);
        yield return Weak("ApprovalWorkflow.Enabled", x => x.ApprovalWorkflow.Enabled = false);
        yield return Weak("ApprovalWorkflow.CoversMigrationJobs", x => x.ApprovalWorkflow.CoversMigrationJobs = false);
        yield return Weak("ApprovalWorkflow.CoversCustomSqlRules", x => x.ApprovalWorkflow.CoversCustomSqlRules = false);
        yield return Weak("ApprovalWorkflow.CoversRecipientDestinations", x => x.ApprovalWorkflow.CoversRecipientDestinations = false);
        yield return Weak("ApprovalWorkflow.CoversDataSourceConnections", x => x.ApprovalWorkflow.CoversDataSourceConnections = false);
        yield return Weak("ApprovalWorkflow.CoversLlmConnection", x => x.ApprovalWorkflow.CoversLlmConnection = false);
        yield return Weak("ApprovalWorkflow.CoversAdminRoleGrants", x => x.ApprovalWorkflow.CoversAdminRoleGrants = false);
        yield return Weak("ApprovalWorkflow.AllowSelfApproval", x => x.ApprovalWorkflow.AllowSelfApproval = true);
    }

    private static TestCaseData Weak(string setting, Action<BeaconConfiguration> weaken)
    {
        return new TestCaseData(setting, weaken).SetArgDisplayNames(setting);
    }

    private static DeploymentProfileStartupReport Report(LogRecorder recorder, DeploymentProfileOptions profile)
    {
        return new DeploymentProfileStartupReport(
            recorder.For<DeploymentProfileStartupReport>(),
            Options.Create(profile),
            new ServiceCollection().BuildServiceProvider());
    }

    private static McpDeploymentOptions Strong()
    {
        return new McpDeploymentOptions
        {
            ForbidRowDataToLlm = true,
            Audit = new McpAuditOptions { Required = true },
            Ceilings = new McpCeilingOptions
            {
                MaxRowLimit = 1000,
                StatementTimeoutSeconds = 30,
                MaxResultBytes = 262144,
                MaxConcurrentQueriesPerKey = 4
            }
        };
    }

    // The code configuration the regulated profile requires: user management and the approval workflow over every kind.
    private static void RegulatedCode(BeaconConfiguration configuration)
    {
        configuration.AddBeaconScheduler<NoOpScheduler>();
        configuration.EnableUserManagement();
        configuration.EnableApprovalWorkflow(x =>
        {
            x.CoversDataSourceConnections = true;
            x.CoversLlmConnection = true;
            x.CoversAdminRoleGrants = true;
        });
    }

    // Settings that meet every requirement, as a host that set them all itself; the host configuration's keys can be overridden.
    private static RegulatedProfileSettings Compliant(
        Dictionary<string, string?>? overrides = null,
        OidcAuthenticationOptions? oidc = null,
        bool embed = false,
        bool migrations = false)
    {
        var beacon = new BeaconConfiguration();
        RegulatedCode(beacon);
        beacon.UserManagement.LocalLogin.Enabled = false;
        beacon.UserManagement.MinimumPasswordLength = RegulatedProfile.MinimumPasswordLength;

        var values = new Dictionary<string, string?>
        {
            ["Beacon:Security:OriginGuard"] = "Enforce",
            ["Beacon:PublicBaseUrl"] = PublicBaseUrl,
            ["Beacon:Authentication:Cookie:SecureAlways"] = "true",
            ["Beacon:Encryption:AllowLegacyCbc"] = "false"
        };
        foreach (var pair in overrides ?? [])
        {
            values[pair.Key] = pair.Value;
        }

        return new RegulatedProfileSettings
        {
            Mcp = Strong(),
            Retention = new RetentionOptions { Locked = true },
            Oidc = oidc,
            Beacon = beacon,
            Audit = new SecurityAuditOptions
            {
                Required = true,
                ExecutionLog = new ExecutionLogOptions { Required = true },
                IntegrityKey = IntegrityKey
            },
            Transport = new DataSourceTransportOptions { EnforceOnExisting = true },
            Outbound = new DataSourceOutboundOptions { AllowPrivateNetworks = false },
            ProjectAuthorization = new ProjectAuthorizationOptions { RequireProjectMembership = true },
            ApiKeys = new ApiKeyOptions { EnforceMaxLifetimeOnExistingKeys = true },
            Embed = new EmbedOptions { Enabled = embed },
            Migrations = new MigrationOptions { Enabled = migrations },
            Configuration = Configuration(values)
        };
    }

    // The regulated profile with the settings it never fills set as it requires.
    private static Dictionary<string, string?> Regulated()
    {
        return new Dictionary<string, string?>
        {
            ["Beacon:Profile"] = "Regulated",
            ["Beacon:UserManagement:LocalLogin:Enabled"] = "false",
            ["Beacon:Retention:Days"] = "90",
            ["Beacon:Retention:ContentDays"] = "30",
            ["Beacon:Mcp:Audit:RetentionDays"] = "365",
            ["Beacon:Security:OriginGuard"] = "Enforce",
            ["Beacon:PublicBaseUrl"] = PublicBaseUrl,
            ["Beacon:Audit:IntegrityKey"] = IntegrityKey,
            ["Beacon:Encryption:AllowLegacyCbc"] = "false",
            ["Beacon:DataSources:Transport:EnforceOnExisting"] = "true",
            ["Beacon:DataSources:Outbound:AllowPrivateNetworks"] = "false",
            ["Beacon:Authorization:RequireProjectMembership"] = "true",
            ["Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys"] = "true"
        };
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    // Beacon's own registration, as a regulated host calls it (weaken changes its code configuration afterwards); withOidc
    // binds the SSO options as Beacon.Api does. Project memberships come from a stand-in source, as no database is reached.
    private static ServiceProvider Build(Dictionary<string, string?> settings, bool withOidc = false, Action<BeaconConfiguration>? weaken = null)
    {
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        settings["Beacon:EncryptionKey"] = TestEncryptionKey.Value;
        var configuration = Configuration(settings);
        var services = new ServiceCollection();
        services.AddLogging(x => x.ClearProviders());
        services.AddMemoryCache();
        services.AddSingleton(configuration);
        services.AddSingleton(Mock.Of<IProjectMembershipSource>(x => x.IsAvailable == true));
        services.AddBeaconServices(configuration, x =>
        {
            RegulatedCode(x);
            weaken?.Invoke(x);
        });

        if (withOidc)
        {
            services.Configure<OidcAuthenticationOptions>(configuration.GetSection("Beacon:Authentication:Oidc"));
        }

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
