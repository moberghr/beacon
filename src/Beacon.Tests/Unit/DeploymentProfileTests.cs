using Beacon.Api;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Configuration;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class DeploymentProfileTests
{
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
        var profile = provider.GetRequiredService<IOptions<DeploymentProfileOptions>>().Value;
        profile.Profile.Should().Be(DeploymentProfile.None);
        profile.AppliedDefaults.Should().BeEmpty();
    }

    [Test]
    public void NoProfile_AcceptsWeakSettings()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Beacon:Mcp:ForceReadOnly"] = "false",
            ["Beacon:RateLimits:Login:PermitLimit"] = "0",
            ["Beacon:Retention:Locked"] = "false"
        });

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
        provider.GetRequiredService<IOptions<DeploymentProfileOptions>>().Value.AppliedDefaults.Should().Equal(
            "Beacon:Mcp:Audit:Required",
            "Beacon:Mcp:ForbidRowDataToLlm",
            "Beacon:Retention:Locked",
            "Beacon:Mcp:Ceilings:MaxRowLimit",
            "Beacon:Mcp:Ceilings:StatementTimeoutSeconds",
            "Beacon:Mcp:Ceilings:MaxResultBytes",
            "Beacon:Mcp:Ceilings:MaxConcurrentQueriesPerKey");
    }

    [Test]
    public void Regulated_KeepsTheValuesTheHostSet()
    {
        var settings = Regulated();
        settings["Beacon:Mcp:Ceilings:MaxRowLimit"] = "5000";
        settings["Beacon:Mcp:Audit:Required"] = "true";
        using var provider = Build(settings);

        provider.GetRequiredService<IStartupValidator>().Validate();

        provider.GetRequiredService<IOptions<McpDeploymentOptions>>().Value.Ceilings.MaxRowLimit.Should().Be(5000);
        provider.GetRequiredService<IOptions<DeploymentProfileOptions>>().Value.AppliedDefaults.Should()
            .NotContain(["Beacon:Mcp:Ceilings:MaxRowLimit", "Beacon:Mcp:Audit:Required"]);
    }

    [TestCase("Beacon:Mcp:ForceReadOnly", "false")]
    [TestCase("Beacon:Mcp:ForcePiiDetection", "false")]
    [TestCase("Beacon:Mcp:Audit:Required", "false")]
    [TestCase("Beacon:Mcp:ForbidRowDataToLlm", "false")]
    [TestCase("Beacon:Retention:Locked", "false")]
    [TestCase("Beacon:RateLimits:Login:PermitLimit", "0")]
    [TestCase("Beacon:RateLimits:Api:PermitLimit", "0")]
    [TestCase("Beacon:RateLimits:Anonymous:PermitLimit", "0")]
    [TestCase("Beacon:RateLimits:Mcp:PermitLimit", "0")]
    [TestCase("Beacon:RateLimits:Ask:PermitLimit", "0")]
    [TestCase("Beacon:RateLimits:Lockout:Enabled", "false")]
    [TestCase("Beacon:UserManagement:LocalLogin:Enabled", "true")]
    public void Regulated_RefusesASettingTheHostSetWeaker(string key, string value)
    {
        var settings = Regulated();
        settings[key] = value;
        using var provider = Build(settings);

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        var expected = key.StartsWith("Beacon:RateLimits:Lockout", StringComparison.Ordinal) ? "Beacon:RateLimits:Lockout" : key;
        startup.Should().Throw<OptionsValidationException>().WithMessage($"*Beacon:Profile is Regulated, so {expected} must*");
    }

    [Test]
    public void Regulated_ListsEveryRefusedSettingInOneFailure()
    {
        var settings = Regulated();
        settings["Beacon:Mcp:ForceReadOnly"] = "false";
        settings["Beacon:Mcp:ForbidRowDataToLlm"] = "false";
        settings["Beacon:RateLimits:Mcp:PermitLimit"] = "0";
        using var provider = Build(settings);

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        var failures = startup.Should().Throw<OptionsValidationException>().Which.Failures;
        failures.Should().Contain(x => x.Contains("Beacon:Mcp:ForceReadOnly must be true"));
        failures.Should().Contain(x => x.Contains("Beacon:Mcp:ForbidRowDataToLlm must be true"));
        failures.Should().Contain(x => x.Contains("Beacon:RateLimits:Mcp:PermitLimit must be greater than zero"));
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
    public void Assess_IgnoresMultiFactorAuthenticationWhileSingleSignOnIsOff()
    {
        var findings = RegulatedProfile.Assess(
            Strong(),
            new RetentionOptions { Locked = true },
            new RateLimitOptions(),
            new OidcAuthenticationOptions { Enabled = false, RequireMfa = false },
            new LocalLoginOptions { Enabled = false });

        findings.Should().BeEmpty();
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
        var report = Report(recorder, new DeploymentProfileOptions(), new McpDeploymentOptions(), new RetentionOptions(), new RateLimitOptions(), new LocalLoginOptions());

        report.Report();

        var entry = recorder.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.EventId.Should().Be(DeploymentProfileStartupReport.WeakSettingsEvent);
        entry.Message.Should().Contain("8 security settings are weaker than the regulated profile requires (profile: None)")
            .And.Contain("Beacon:Mcp:Audit:Required must be true")
            .And.Contain("Beacon:Mcp:ForbidRowDataToLlm must be true")
            .And.Contain("Beacon:Mcp:Ceilings:MaxRowLimit must be set")
            .And.Contain("Beacon:Mcp:Ceilings:StatementTimeoutSeconds must be set")
            .And.Contain("Beacon:Mcp:Ceilings:MaxResultBytes must be set")
            .And.Contain("Beacon:Mcp:Ceilings:MaxConcurrentQueriesPerKey must be set")
            .And.Contain("Beacon:Retention:Locked must be true")
            .And.Contain("Beacon:UserManagement:LocalLogin:Enabled must be false")
            .And.Contain("Set Beacon:Profile to Regulated");
        entry.Message.Should().NotContain("Beacon:Mcp:ForceReadOnly", "a lock that is on is not weak");
    }

    [Test]
    public void Report_WithoutAProfile_LogsNothingWhenEverySettingHolds()
    {
        var recorder = new LogRecorder();
        var report = Report(recorder, new DeploymentProfileOptions(), Strong(), new RetentionOptions { Locked = true }, new RateLimitOptions(), new LocalLoginOptions { Enabled = false });

        report.Report();

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
        var report = Report(recorder, profile, Strong(), new RetentionOptions { Locked = true }, new RateLimitOptions(), new LocalLoginOptions { Enabled = false });

        report.Report();

        var entry = recorder.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Information);
        entry.EventId.Should().Be(DeploymentProfileStartupReport.ProfileHoldsEvent);
        entry.Message.Should().Contain("Beacon:Profile is Regulated and every setting it requires holds")
            .And.Contain("Beacon:Mcp:Audit:Required, Beacon:Retention:Locked");
    }

    [Test]
    public void Report_NeverCarriesAConfiguredValue()
    {
        var recorder = new LogRecorder();
        var oidc = new OidcAuthenticationOptions { Enabled = true, ClientSecret = "client-secret-value", Authority = "https://login.example.test/tenant" };
        var localLogin = new LocalLoginOptions { Enabled = true, BreakGlassUserNames = ["break-glass-name"] };
        var report = Report(recorder, new DeploymentProfileOptions(), new McpDeploymentOptions(), new RetentionOptions(), new RateLimitOptions(), localLogin, oidc);

        report.Report();

        var text = recorder.Entries.Should().ContainSingle().Which.FullText;
        text.Should().Contain("Beacon:Authentication:Oidc:RequireMfa must be true");
        text.Should().NotContain("client-secret-value").And.NotContain("login.example.test").And.NotContain("break-glass-name");
    }

    private static DeploymentProfileStartupReport Report(
        LogRecorder recorder,
        DeploymentProfileOptions profile,
        McpDeploymentOptions mcp,
        RetentionOptions retention,
        RateLimitOptions rateLimits,
        LocalLoginOptions localLogin,
        OidcAuthenticationOptions? oidc = null)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.LocalLogin = localLogin;

        return new DeploymentProfileStartupReport(
            recorder.For<DeploymentProfileStartupReport>(),
            Options.Create(profile),
            Options.Create(mcp),
            Options.Create(retention),
            Options.Create(rateLimits),
            configuration,
            oidc == null ? null : Options.Create(oidc));
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

    // The regulated profile with the settings it never fills set as it requires.
    private static Dictionary<string, string?> Regulated()
    {
        return new Dictionary<string, string?>
        {
            ["Beacon:Profile"] = "Regulated",
            ["Beacon:UserManagement:LocalLogin:Enabled"] = "false",
            ["Beacon:Retention:Days"] = "90",
            ["Beacon:Retention:ContentDays"] = "30",
            ["Beacon:Mcp:Audit:RetentionDays"] = "365"
        };
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    // Beacon's own registration, as a host calls it; withOidc binds the SSO options as Beacon.Api does.
    private static ServiceProvider Build(Dictionary<string, string?> settings, bool withOidc = false)
    {
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        settings["Beacon:EncryptionKey"] = TestEncryptionKey.Value;
        var configuration = Configuration(settings);
        var services = new ServiceCollection();
        services.AddLogging(x => x.ClearProviders());
        services.AddMemoryCache();
        services.AddSingleton(configuration);
        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());

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
