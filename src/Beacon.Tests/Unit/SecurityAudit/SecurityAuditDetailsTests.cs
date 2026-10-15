using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// What an event's details may hold — ids, counts, flags, times and codes, never free text, and nothing under a key that
/// names text, for every fragment that does — and the
/// <c>Beacon:Audit</c> options a deployment is validated against at startup: a retention under a year (or over a hundred
/// years) and an unusable integrity key fail the host, without the key in the message.
/// </summary>
[TestFixture]
public class SecurityAuditDetailsTests
{
    // The key fragments that name text: a value under a key holding one is withheld, however code-like it looks.
    private static readonly string[] TextKeyFragments =
    [
        "name", "email", "mail", "sql", "question", "comment", "note", "description", "text", "message", "destination",
        "url", "token", "secret", "password", "header", "body", "address", "agent", "subject", "externalid", "login"
    ];

    [Test]
    public void FreeText_AndAnyStringUnderAKeyNamingText_IsWithheld_WhileIdsFlagsAndCodesAreKept()
    {
        var details = new SecurityAuditDetails()
            .Add("roleIds", [3, 3, 4])
            .Add("hasAccessToken", true)
            .Add("count", 2L)
            .AddCode("reason", "local_login_off")
            .AddCode("reasonText", "Password for ana@example.com was wrong")
            .AddCode("userName", "ana")
            .AddCode("destination", "hooks.example.com")
            .AddCode("sql", "select")
            .AddCode("statusNote", "ok");

        var json = details.ToJson();

        json.Should().Be(
            "{\"roleIds\":[3,4],\"hasAccessToken\":true,\"count\":2,\"reason\":\"local_login_off\",\"reasonText\":\"[withheld]\"," +
            "\"userName\":\"[withheld]\",\"destination\":\"[withheld]\",\"sql\":\"[withheld]\",\"statusNote\":\"[withheld]\"}");
        details.Withheld.Should().BeEquivalentTo(["reasonText", "userName", "destination", "sql", "statusNote"]);
    }

    [Test]
    public void TheFragmentsThatNameText_AreTheOnesTheDetailsWithholdUnder()
    {
        var used = typeof(SecurityAuditDetails)
            .GetField("TextKeyFragments", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null);

        used.Should().BeEquivalentTo(TextKeyFragments, "a fragment dropped from the details lets a value under it through");
    }

    [TestCaseSource(nameof(TextKeyFragments))]
    public void ACodeUnderAKeyNamingText_IsWithheld_WhereverTheFragmentSits_AndInAnyCase(string fragment)
    {
        var pascal = char.ToUpperInvariant(fragment[0]) + fragment[1..];
        string[] keys = [fragment, "owner" + pascal, "x" + pascal + "Count"];

        foreach (var key in keys)
        {
            var single = new SecurityAuditDetails().AddCode(key, "ok");
            var list = new SecurityAuditDetails().AddCodes(key, ["ok", "v1"]);

            single.ToJson().Should().Be($"{{\"{key}\":\"[withheld]\"}}", $"'{key}' names text");
            single.Withheld.Should().Equal(key);
            list.ToJson().Should().Be($"{{\"{key}\":[\"[withheld]\",\"[withheld]\"]}}", $"'{key}' names text");
            list.Withheld.Should().Equal(key);
        }

        new SecurityAuditDetails().AddCode("ownerKind", "ok").ToJson().Should().Be("{\"ownerKind\":\"ok\"}", "a key naming no text keeps its code");
    }

    [Test]
    public void LongIdLists_KeepTheirFirstEntries_AndTheirCount_AndTheWholeStaysBounded()
    {
        var details = new SecurityAuditDetails().Add("dataSourceIds", Enumerable.Range(1, 120));

        var json = details.ToJson()!;

        json.Should().Contain("\"dataSourceIdsCount\":120");
        json.Length.Should().BeLessThanOrEqualTo(SecurityAuditDetails.MaxJsonLength);
        new SecurityAuditDetails().ToJson().Should().BeNull("an event without details stores none");
    }

    [TestCase("UserName")]
    [TestCase("user name")]
    [TestCase("a-key")]
    public void AKeyThatIsNoCamelCaseIdentifier_IsAProgrammingError(string key)
    {
        var act = () => new SecurityAuditDetails().Add(key, 1);

        act.Should().Throw<ArgumentException>();
    }

    [TestCase(364, false)]
    [TestCase(365, true)]
    [TestCase(36500, true)]
    [TestCase(36501, false)]
    [TestCase(null, true)]
    public void TheRetention_IsAtLeastAYear_OrUnset(int? days, bool valid)
    {
        var result = new SecurityAuditOptionsValidator().Validate(null, new SecurityAuditOptions { RetentionDays = days });

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            result.FailureMessage.Should().Contain("Beacon:Audit:RetentionDays");
        }
    }

    [Test]
    public void AShortOrPlaceholderIntegrityKey_IsRefused_WithoutNamingIt()
    {
        var shortKey = new SecurityAuditOptionsValidator().Validate(null, new SecurityAuditOptions { IntegrityKey = "short-key" });
        var placeholder = new SecurityAuditOptionsValidator().Validate(null, new SecurityAuditOptions { PreviousIntegrityKeys = ["${OLD_AUDIT_KEY_FROM_THE_VAULT_PLEASE}"] });

        shortKey.Succeeded.Should().BeFalse();
        shortKey.FailureMessage.Should().NotContain("short-key");
        placeholder.Succeeded.Should().BeFalse();
        placeholder.FailureMessage.Should().Contain("PreviousIntegrityKeys").And.NotContain("OLD_AUDIT_KEY");
    }

    [Test]
    public void ARetentionUnderAYear_FailsTheHostAtStartup()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["Beacon:Audit:RetentionDays"] = "90" });

        var act = () => provider.GetRequiredService<IOptions<SecurityAuditOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*RetentionDays*");
        provider.GetServices<IValidateOptions<SecurityAuditOptions>>().Should().ContainSingle();
    }

    [Test]
    public void ByDefault_EventsAreKeptForever()
    {
        using var provider = BuildProvider([]);

        provider.GetRequiredService<IOptions<SecurityAuditOptions>>().Value.RetentionDays.Should().BeNull();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        settings["Beacon:EncryptionKey"] = TestEncryptionKey.Value;
        var services = new ServiceCollection();
        services.AddBeaconServices(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), x => x.AddBeaconScheduler<NoOpScheduler>());

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }

    [Test]
    public void ACodeList_KeepsCodes_WithholdsEachValueThatIsNot_AndCountsALongList()
    {
        var details = new SecurityAuditDetails()
            .AddCodes("changedKeys", ["Mcp.MaxRowLimit", "Mcp.MaxRowLimit", "not a code at all", "LLM.ApiKey"])
            .AddCodes("manyKeys", Enumerable.Range(0, SecurityAuditDetails.MaxListLength + 3).Select(x => $"k{x}"));

        var json = details.ToJson()!;

        json.Should().StartWith("{\"changedKeys\":[\"Mcp.MaxRowLimit\",\"[withheld]\",\"LLM.ApiKey\"],");
        json.Should().Contain($"\"manyKeysCount\":{SecurityAuditDetails.MaxListLength + 3}");
        details.Withheld.Should().Equal("changedKeys");
    }

    [Test]
    public void ACodeList_UnderAKeyNamingText_IsWithheldWhole()
    {
        var details = new SecurityAuditDetails().AddCodes("recipientNames", ["ops", "finance"]);

        details.ToJson().Should().Be("{\"recipientNames\":[\"[withheld]\",\"[withheld]\"]}");
    }

    [Test]
    public void DetailsOverTheBound_KeepEverySingleValue_AndShortenTheLongestListFirst_RecordingItsLength()
    {
        // Three lists of 50 long codes are far over the bound; the single values and the short list must survive.
        var details = new SecurityAuditDetails()
            .Add("userId", 7)
            .AddCode("reason", "invalid_credentials")
            .Add("enabled", true)
            .Add("projectIds", [1, 2, 3])
            .AddCodes("longest", Enumerable.Range(0, 50).Select(x => $"code-{x:D3}-{new string('x', 50)}"))
            .AddCodes("longer", Enumerable.Range(0, 50).Select(x => $"code-{x:D3}-{new string('y', 30)}"));

        var json = details.ToJson()!;

        json.Length.Should().BeLessThanOrEqualTo(SecurityAuditDetails.MaxJsonLength);
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        parsed["truncated"]!.GetValue<bool>().Should().BeTrue();
        parsed["userId"]!.GetValue<int>().Should().Be(7);
        parsed["reason"]!.GetValue<string>().Should().Be("invalid_credentials");
        parsed["enabled"]!.GetValue<bool>().Should().BeTrue();
        parsed["projectIds"]!.AsArray().Should().HaveCount(3, "the short list is not shortened while longer ones can be");
        parsed["longestCount"]!.GetValue<int>().Should().Be(50);
        parsed["longest"]!.AsArray().Count.Should().BeLessThan(50);
        parsed["longest"]!.AsArray()[0]!.GetValue<string>().Should().StartWith("code-000", "a list keeps its first entries");
    }
}
