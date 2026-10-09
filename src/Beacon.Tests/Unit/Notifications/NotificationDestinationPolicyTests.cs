using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// Where a recipient may point: https only, the vendors' own hosts for Slack/Teams/Jira (extendable by configuration),
/// any public host for webhooks unless configured, bare and optionally domain-restricted email, disabled types, the
/// explicit webhook header allow-list, and the options validation that guards all of it.
/// </summary>
[TestFixture]
public class NotificationDestinationPolicyTests
{
    [TestCase(NotificationType.Slack, "https://hooks.slack.com/services/T0/B0/x")]
    [TestCase(NotificationType.Slack, "https://HOOKS.SLACK.COM/services/T0/B0/x")]
    [TestCase(NotificationType.Slack, "https://hooks.slack.com./services/T0/B0/x")]
    [TestCase(NotificationType.Slack, "https://hooks.slack.com:443/services/T0/B0/x")]
    [TestCase(NotificationType.Teams, "https://contoso.webhook.office.com/webhookb2/abc")]
    [TestCase(NotificationType.Teams, "https://prod-12.westeurope.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?sig=x")]
    [TestCase(NotificationType.Teams, "https://default0123.d1.environment.api.powerplatform.com:443/powerautomate/automations/direct/workflows/abc/triggers/manual/paths/invoke?sig=x")]
    [TestCase(NotificationType.Jira, "acme;SEC;svc@acme.eu;token")]
    [TestCase(NotificationType.Jira, "https://acme.atlassian.net;SEC;svc@acme.eu;token")]
    [TestCase(NotificationType.Jira, "https://api.atlassian.com/ex/jira/1234-abcd;SEC;svc@acme.eu;token")]
    [TestCase(NotificationType.Webhook, "https://hooks.example.com/in?token=x")]
    [TestCase(NotificationType.Webhook, "https://93.184.216.34/in")]
    [TestCase(NotificationType.Email, "ops@example.com")]
    public void Default_AllowsVendorHostsAndPublicDestinations(NotificationType type, string destination)
    {
        var act = () => NotificationTestKit.Policy().EnsureAllowed(type, destination);

        act.Should().NotThrow();
    }

    [TestCase(NotificationType.Slack, "http://hooks.slack.com/services/T0/B0/x", "*https URL*")]
    [TestCase(NotificationType.Slack, "https://hooks.slack.com.unlisted.example/services/x", "*not allowed for Slack*")]
    [TestCase(NotificationType.Slack, "https://unlistedhooks.slack.com/services/x", "*not allowed for Slack*")]
    [TestCase(NotificationType.Slack, "https://unlisted.example/services/x", "*not allowed for Slack*")]
    [TestCase(NotificationType.Slack, "https://user:pass@hooks.slack.com/services/x", "*user credentials*")]
    [TestCase(NotificationType.Slack, "https://hooks.slack.com@unlisted.example/services/x", "*user credentials*")]
    [TestCase(NotificationType.Slack, "https://hooks.slack.com%2eunlisted.example/x", "*")]
    [TestCase(NotificationType.Slack, "https://unlisted.example#hooks.slack.com", "*not allowed for Slack*")]
    [TestCase(NotificationType.Slack, "https://unlisted.example\\@hooks.slack.com/x", "*")]
    [TestCase(NotificationType.Slack, "https://hооks.slack.com/services/x", "*not allowed for Slack*")]
    [TestCase(NotificationType.Teams, "https://webhook.office.com/x", "*not allowed for Teams*")]
    [TestCase(NotificationType.Teams, "https://outlook.office.com/webhook/x", "*not allowed for Teams*")]
    [TestCase(NotificationType.Teams, "https://contoso.webhook.office.com.unlisted.example/x", "*not allowed for Teams*")]
    [TestCase(NotificationType.Teams, "hooks.slack.com/x", "*https URL*")]
    [TestCase(NotificationType.Webhook, "http://hooks.example.com/in", "*https URL*")]
    [TestCase(NotificationType.Webhook, "ftp://hooks.example.com/in", "*https URL*")]
    [TestCase(NotificationType.Email, "not-an-address", "*email addresses*")]
    [TestCase(NotificationType.Email, "ops@example.com, broken", "*email addresses*")]
    [TestCase(NotificationType.Email, "Ops Team <ops@example.com>", "*email addresses*")]
    [TestCase(NotificationType.Email, "Ops<ops@example.com>", "*email addresses*")]
    [TestCase(NotificationType.Email, "\"Ops\"<ops@example.com>", "*email addresses*")]
    [TestCase(NotificationType.Email, "ops@[127.0.0.1]", "*email addresses*")]
    [TestCase(NotificationType.Email, "ops @example.com", "*email addresses*")]
    [TestCase(NotificationType.Email, " ", "*required*")]
    public void Default_RefusesOtherDestinations(NotificationType type, string destination, string reason)
    {
        var act = () => NotificationTestKit.Policy().EnsureAllowed(type, destination);

        act.Should().Throw<InvalidOperationException>().WithMessage(reason);
    }

    // Every spelling of a non-public address in a webhook URL is refused when saved.
    [TestCase("https://127.0.0.1/in")]
    [TestCase("https://2130706433/in")]
    [TestCase("https://0x7f.1/in")]
    [TestCase("https://0177.0.0.1/in")]
    [TestCase("https://127.1/in")]
    [TestCase("https://0.0.0.0/in")]
    [TestCase("https://169.254.169.254/latest/meta-data")]
    [TestCase("https://10.0.0.5:8080/admin")]
    [TestCase("https://[::1]/in")]
    [TestCase("https://[::]/in")]
    [TestCase("https://[::ffff:127.0.0.1]/in")]
    [TestCase("https://[64:ff9b::a9fe:a9fe]/in")]
    [TestCase("https://[fd00:ec2::254]/in")]
    [TestCase("https://localhost/in")]
    [TestCase("https://LOCALHOST/in")]
    [TestCase("https://localhost./in")]
    [TestCase("https://api.localhost/in")]
    public void Webhook_NonPublicAddressLiteral_IsRefused(string destination)
    {
        var act = () => NotificationTestKit.Policy().EnsureAllowed(NotificationType.Webhook, destination);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Webhook_PrivateNetworkForWebhooks_LetsTheLiteralThrough()
    {
        var policy = NotificationTestKit.Policy(NotificationTestKit.PrivateNetworks(NotificationType.Webhook, "10.20.0.0/16", "localhost"));

        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://10.20.1.2/in")).Should().NotThrow();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://[::ffff:10.20.1.2]/in")).Should().NotThrow();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://localhost/in")).Should().NotThrow();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://10.21.1.2/in")).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Webhook_PrivateNetworkForAnotherType_DoesNotApply()
    {
        var policy = NotificationTestKit.Policy(NotificationTestKit.PrivateNetworks(NotificationType.Jira, "10.20.0.0/16", "localhost"));

        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://10.20.1.2/in")).Should().Throw<InvalidOperationException>();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://localhost/in")).Should().Throw<InvalidOperationException>();
    }

    [TestCase("unlisted.example/x#;SEC;svc@acme.eu;token")]
    [TestCase("x@unlisted.example:443/;SEC;svc@acme.eu;token")]
    [TestCase("http://acme.atlassian.net;SEC;svc@acme.eu;token")]
    [TestCase("https://unlisted.example;SEC;svc@acme.eu;token")]
    [TestCase("https://acme.atlassian.net.unlisted.example;SEC;svc@acme.eu;token")]
    [TestCase("https://user@acme.atlassian.net;SEC;svc@acme.eu;token")]
    [TestCase("acme;SEC;svc@acme.eu")]
    public void Jira_RefusesSitesOutsideAtlassian(string destination)
    {
        var act = () => NotificationTestKit.Policy().EnsureAllowed(NotificationType.Jira, destination);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ConfiguredHosts_ExtendTheBuiltInHosts()
    {
        var policy = NotificationTestKit.Policy(new NotificationChannelOptions
        {
            AllowedHosts = new NotificationAllowedHosts { Jira = ["jira.bank.example"], Teams = ["*.webhook.office365.us"] },
        });

        policy.Invoking(x => x.EnsureAllowed(NotificationType.Jira, "https://jira.bank.example;SEC;svc@bank.eu;token")).Should().NotThrow();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Jira, "acme;SEC;svc@acme.eu;token")).Should().NotThrow("the built-in hosts still apply");
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Teams, "https://gov.webhook.office365.us/x")).Should().NotThrow();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Jira, "https://other.bank.example;SEC;svc@bank.eu;token")).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ConfiguredWebhookHosts_AreTheWholeWebhookAllowList()
    {
        var policy = NotificationTestKit.Policy(new NotificationChannelOptions
        {
            AllowedHosts = new NotificationAllowedHosts { Webhook = ["hooks.bank.example", "*.partner.example"] },
        });

        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://hooks.bank.example/in")).Should().NotThrow();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://a.b.partner.example/in")).Should().NotThrow();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://partner.example/in")).Should().Throw<InvalidOperationException>();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://hooks.bank.example.unlisted.example/in")).Should().Throw<InvalidOperationException>();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Webhook, "https://hooks.example.com/in")).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Email_IsNormalisedToABareCommaSeparatedList()
    {
        NotificationTestKit.Policy().EnsureAllowed(NotificationType.Email, " ops@example.com ;risk@example.org, ")
            .Should().Be("ops@example.com,risk@example.org");
    }

    [Test]
    public void ConfiguredEmailDomains_RestrictEveryAddress()
    {
        var policy = NotificationTestKit.Policy(new NotificationChannelOptions
        {
            AllowedHosts = new NotificationAllowedHosts { Email = ["bank.example", "*.bank.example"] },
        });

        policy.Invoking(x => x.EnsureAllowed(NotificationType.Email, "ops@bank.example; risk@eu.bank.example")).Should().NotThrow();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Email, "ops@bank.example, someone@mail.example"))
            .Should().Throw<InvalidOperationException>().WithMessage("*domain*");
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Email, "ops@xbank.example")).Should().Throw<InvalidOperationException>();
        policy.Invoking(x => x.EnsureAllowed(NotificationType.Email, "ops@bank.example.unlisted.example")).Should().Throw<InvalidOperationException>();
    }

    [TestCase(NotificationType.Webhook, "https://hooks.example.com/in")]
    [TestCase(NotificationType.Email, "ops@example.com")]
    public void DisabledTypes_AreRefused(NotificationType type, string destination)
    {
        var policy = NotificationTestKit.Policy(new NotificationChannelOptions { DisabledTypes = [type] });

        policy.Invoking(x => x.EnsureAllowed(type, destination))
            .Should().Throw<InvalidOperationException>().WithMessage($"*'{type}' are disabled*");
    }

    [Test]
    public void Messages_NeverEchoTheDestination()
    {
        var act = () => NotificationTestKit.Policy().EnsureAllowed(NotificationType.Slack, "https://unlisted.example/services/T0/B0/PATHTOKEN");

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("PATHTOKEN").And.NotContain("unlisted.example");
    }

    [TestCase("{\"Authorization\":\"Bearer t\"}")]
    [TestCase("{\"api-key\":\"k\",\"X-Api-Key\":\"k\",\"Ocp-Apim-Subscription-Key\":\"s\"}")]
    [TestCase("{}")]
    [TestCase("")]
    [TestCase(null)]
    public void Headers_OnTheBuiltInAllowList_AreAccepted(string? headersJson)
    {
        var act = () => NotificationTestKit.Policy().ParseHeaders(headersJson);

        act.Should().NotThrow();
    }

    [TestCase("{\"X-Team\":\"ops\"}", "*'X-Team' is not allowed*")]
    [TestCase("{\"Host\":\"internal\"}", "*'Host' is not allowed*")]
    [TestCase("{\"Content-Length\":\"0\"}", "*not allowed*")]
    [TestCase("{\"Connection\":\"close\"}", "*not allowed*")]
    [TestCase("{\"Transfer-Encoding\":\"chunked\"}", "*not allowed*")]
    [TestCase("{\"Cookie\":\"a=b\"}", "*not allowed*")]
    [TestCase("{\"Metadata-Flavor\":\"Google\"}", "*not allowed*")]
    [TestCase("{\"X-Forwarded-For\":\"127.0.0.1\"}", "*not allowed*")]
    [TestCase("{\"X-HTTP-Method-Override\":\"DELETE\"}", "*not allowed*")]
    [TestCase("{\"Authorization\":\"a\\r\\nHost: internal\"}", "*control characters*")]
    [TestCase("{\"X Team\":\"a\"}", "*names*")]
    [TestCase("{\"Authorization\":\"a\",\"authorization\":\"b\"}", "*more than once*")]
    [TestCase("{\"Authorization\":1}", "*JSON object of string values*")]
    [TestCase("[\"Authorization\"]", "*JSON object of string values*")]
    [TestCase("not json", "*JSON object of string values*")]
    public void Headers_OffTheAllowList_AreRefused(string headersJson, string reason)
    {
        var act = () => NotificationTestKit.Policy().ParseHeaders(headersJson);

        act.Should().Throw<InvalidOperationException>().WithMessage(reason);
    }

    [Test]
    public void Headers_ConfiguredAllowedHeaders_AreAddedToTheBuiltIns()
    {
        var policy = NotificationTestKit.Policy(new NotificationChannelOptions { AllowedHeaders = ["X-Team", "X-Signature"] });

        policy.Invoking(x => x.ParseHeaders("{\"x-team\":\"ops\",\"X-Signature\":\"s\",\"Authorization\":\"t\"}")).Should().NotThrow();
        policy.Invoking(x => x.ParseHeaders("{\"X-Other\":\"o\"}")).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Headers_OverTheCountLimit_AreRefused()
    {
        var policy = NotificationTestKit.Policy(new NotificationChannelOptions
        {
            AllowedHeaders = [.. Enumerable.Range(0, NotificationDestinationPolicy.MaxHeaders + 1).Select(x => $"X-H{x}")],
        });
        var headers = Enumerable.Range(0, NotificationDestinationPolicy.MaxHeaders + 1)
            .ToDictionary(x => $"X-H{x}", _ => "v");

        var act = () => policy.ParseHeaders(System.Text.Json.JsonSerializer.Serialize(headers));

        act.Should().Throw<InvalidOperationException>().WithMessage("*at most*");
    }

    // --- options validation --------------------------------------------------------------------------------------

    [TestCase("AllowedHosts:Webhook", "https://hooks.example.com")]
    [TestCase("AllowedHosts:Slack", "*")]
    [TestCase("AllowedHosts:Email", "*.com")]
    [TestCase("AllowedHosts:Email", "@bank.example")]
    [TestCase("AllowedPrivateNetworks:Jira", "10.1.2.3/8")]
    [TestCase("AllowedPrivateNetworks:Webhook", "http://jira")]
    [TestCase("AllowedPrivateNetworks:Teams", "*.internal")]
    [TestCase("AllowedHeaders", "Host")]
    [TestCase("AllowedHeaders", "X-Forwarded-For")]
    [TestCase("AllowedHeaders", "Metadata")]
    [TestCase("AllowedHeaders", "X Team")]
    [TestCase("Nat64Prefixes", "2001:db8:64::/64")]
    [TestCase("Nat64Prefixes", "10.0.0.0/8")]
    public void OptionsValidator_NamesEachInvalidEntry(string key, string entry)
    {
        var options = new NotificationChannelOptions();
        Section(options, key).Add(entry);

        var result = Validator().Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain($"Beacon:Notifications:{key}").And.Contain($"'{entry}'");
    }

    [Test]
    public void OptionsValidator_ReportsEveryInvalidEntry()
    {
        var options = new NotificationChannelOptions
        {
            AllowedHosts = new NotificationAllowedHosts { Webhook = ["https://hooks.example.com"], Slack = ["*"] },
            AllowedHeaders = ["Host"],
        };
        options.AllowedPrivateNetworks.Jira.Add("10.1.2.3/8");

        var result = Validator().Validate(Options.DefaultName, options);

        result.Failures.Should().HaveCount(4);
    }

    [Test]
    public void OptionsValidator_ProxyModeWithoutWebhookHosts_Fails()
    {
        var options = new NotificationChannelOptions { UseSystemProxy = true };

        var result = Validator().Validate(Options.DefaultName, options);

        result.Failures.Should().ContainSingle().Which.Should().Contain("UseSystemProxy").And.Contain("AllowedHosts:Webhook");
    }

    [Test]
    public void OptionsValidator_ProxyModeWithWebhookHostsOrWebhookDisabled_Passes()
    {
        var listed = new NotificationChannelOptions { UseSystemProxy = true, AllowedHosts = new NotificationAllowedHosts { Webhook = ["hooks.bank.example"] } };
        var disabled = new NotificationChannelOptions { UseSystemProxy = true, DisabledTypes = [NotificationType.Webhook] };

        Validator().Validate(Options.DefaultName, listed).Succeeded.Should().BeTrue();
        Validator().Validate(Options.DefaultName, disabled).Succeeded.Should().BeTrue();
    }

    [Test]
    public void OptionsValidator_AcceptsTheDefaultsAndWellFormedEntries()
    {
        var options = new NotificationChannelOptions
        {
            AllowedHosts = new NotificationAllowedHosts { Jira = ["jira.bank.example"], Teams = ["*.webhook.office365.us"], Email = ["bank.example"] },
            AllowedHeaders = ["X-Team"],
            Nat64Prefixes = ["2001:db8:64::/96"],
            DisabledTypes = [NotificationType.Webhook],
            RequireEncryptedSecrets = true,
        };
        options.AllowedPrivateNetworks.Jira.AddRange(["10.20.0.0/16", "10.1.2.3", "jira.bank.example"]);

        Validator().Validate(Options.DefaultName, new NotificationChannelOptions()).Succeeded.Should().BeTrue();
        Validator().Validate(Options.DefaultName, options).Succeeded.Should().BeTrue();
    }

    [Test]
    public void OptionsValidator_LogsTheEffectivePolicyWithoutSecrets()
    {
        var logger = new CapturingLogger<NotificationChannelOptionsValidator>();
        var options = new NotificationChannelOptions { DisabledTypes = [NotificationType.Email], AllowedHeaders = ["X-Team"] };
        options.AllowedPrivateNetworks.Jira.Add("10.20.0.0/16");

        new NotificationChannelOptionsValidator(logger).Validate(Options.DefaultName, options);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain("Email").And.Contain("hooks.slack.com").And.Contain("Jira: 1").And.Contain("X-Team");
        entry.Message.Should().NotContain("10.20.0.0", "private ranges are counted, not listed");
    }

    private static NotificationChannelOptionsValidator Validator()
    {
        return new NotificationChannelOptionsValidator(NullLogger<NotificationChannelOptionsValidator>.Instance);
    }

    private static List<string> Section(NotificationChannelOptions options, string key)
    {
        return key switch
        {
            "AllowedHosts:Webhook" => options.AllowedHosts.Webhook,
            "AllowedHosts:Slack" => options.AllowedHosts.Slack,
            "AllowedHosts:Email" => options.AllowedHosts.Email,
            "AllowedPrivateNetworks:Jira" => options.AllowedPrivateNetworks.Jira,
            "AllowedPrivateNetworks:Webhook" => options.AllowedPrivateNetworks.Webhook,
            "AllowedPrivateNetworks:Teams" => options.AllowedPrivateNetworks.Teams,
            "AllowedHeaders" => options.AllowedHeaders,
            _ => options.Nat64Prefixes,
        };
    }
}
