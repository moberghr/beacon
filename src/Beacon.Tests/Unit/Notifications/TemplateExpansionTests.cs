using System.Text.Json;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Mail;
using Beacon.Core.Adapters.Shared;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// Custom body templates expand in one pass over the template, string values are escaped for the JSON channels, and the
/// email subject is one bounded line.
/// </summary>
[TestFixture]
public class TemplateExpansionTests
{
    [Test]
    public void Expand_OrdinaryValues_KeepsTheExistingOutput()
    {
        var data = new Dictionary<string, object?>
        {
            ["SubscriptionName"] = "daily",
            ["TotalRecords"] = 12,
            ["TimedOut"] = true,
            ["Ratio"] = 1.5d,
            ["Timestamp"] = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ["Records"] = new List<int> { 1, 2 },
            ["Missing"] = null,
            ["Anomaly"] = new Dictionary<string, object?> { ["Severity"] = "high" }
        };
        const string template = "{\"n\":\"{{SubscriptionName}}\",\"c\":{{TotalRecords}},\"t\":{{ timedout }},\"r\":{{Ratio}},"
            + "\"ts\":\"{{Timestamp}}\",\"rows\":{{Records}},\"m\":\"{{Missing}}\",\"s\":\"{{Anomaly.Severity}}\",\"u\":\"{{Nope}}\"}";

        var plain = AdapterTemplateProcessor.ProcessTemplate(template, data);
        var json = AdapterTemplateProcessor.ProcessTemplate(template, data, escapeJsonStrings: true);

        plain.Should().Be(
            $"{{\"n\":\"daily\",\"c\":12,\"t\":true,\"r\":{1.5d:F2},\"ts\":\"2026-01-02T03:04:05.0000000Z\",\"rows\":[1,2],\"m\":\"\",\"s\":\"high\",\"u\":\"{{{{UNKNOWN:Nope}}}}\"}}");
        json.Should().Be(plain);
    }

    [Test]
    public void Expand_ValueContainingAPlaceholder_IsNotExpandedAgain()
    {
        var data = new Dictionary<string, object?>
        {
            ["SubscriptionName"] = "{{SqlQuery}}{{SqlQuery}}",
            ["SqlQuery"] = "SELECT 1"
        };

        var result = AdapterTemplateProcessor.ProcessTemplate("a={{SubscriptionName}};b={{SqlQuery}}", data);

        result.Should().Be("a={{SqlQuery}}{{SqlQuery}};b=SELECT 1");
    }

    [Test]
    public void Expand_RepeatedPlaceholder_IsReplacedEverywhere()
    {
        var data = new Dictionary<string, object?> { ["SubscriptionName"] = "x" };

        AdapterTemplateProcessor.ProcessTemplate("{{SubscriptionName}}-{{SubscriptionName}}", data).Should().Be("x-x");
    }

    [Test]
    public void Expand_StringValueInAJsonTemplate_StaysOneJsonString()
    {
        var name = "a\"b\\c\nd\u0001e";
        var data = new Dictionary<string, object?> { ["SubscriptionName"] = name };

        var result = AdapterTemplateProcessor.ProcessTemplate("{\"text\":\"{{SubscriptionName}}\",\"x\":1}", data, escapeJsonStrings: true);

        using var document = JsonDocument.Parse(result);
        document.RootElement.EnumerateObject().Select(x => x.Name).Should().Equal("text", "x");
        document.RootElement.GetProperty("text").GetString().Should().Be(name);
    }

    [Test]
    public void Expand_PrettyAndCollectionPlaceholders_StayRawJson()
    {
        var data = new Dictionary<string, object?>
        {
            ["Records"] = new List<Dictionary<string, object?>> { new() { ["a"] = "x\"y" } },
            ["Records_Pretty"] = new List<int> { 1 }
        };

        var result = AdapterTemplateProcessor.ProcessTemplate("{\"r\":{{Records}},\"p\":{{Records_Pretty}}}", data, escapeJsonStrings: true);

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("r")[0].GetProperty("a").GetString().Should().Be("x\"y");
    }

    [Test]
    public void Expand_OutputBeyondTheCap_Throws()
    {
        var data = new Dictionary<string, object?> { ["SqlQuery"] = new string('x', 600_000) };

        var act = () => AdapterTemplateProcessor.ProcessTemplate("{{SqlQuery}}{{SqlQuery}}", data);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public async Task EmailSubject_IsOneBoundedLine()
    {
        string? subject = null;
        var mail = new Mock<IEmailAdapter>();
        mail.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryResultFile?>()))
            .Callback<string, string, string, QueryResultFile?>((_, s, _, _) => subject = s)
            .Returns(Task.CompletedTask);

        await new EmailAdapter(mail.Object).SendNotificationAsync(Recipient("wh\r\nBcc: x@y.example\u0085z", new string('n', 500)), null);

        subject.Should().NotBeNull();
        subject!.Should().NotContainAny("\r", "\n", "\u0085", "\u2028");
        subject.Length.Should().BeLessThanOrEqualTo(200);
        subject.Should().StartWith("[Beacon] wh Bcc: x@y.example z - ");
    }

    [Test]
    public async Task EmailSubject_OrdinaryNames_KeepTheExistingFormat()
    {
        string? subject = null;
        var mail = new Mock<IEmailAdapter>();
        mail.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryResultFile?>()))
            .Callback<string, string, string, QueryResultFile?>((_, s, _, _) => subject = s)
            .Returns(Task.CompletedTask);

        await new EmailAdapter(mail.Object).SendNotificationAsync(Recipient("warehouse", "daily"), null);

        subject.Should().Be("[Beacon] warehouse - daily");
    }

    private static RecipientQueryResult Recipient(string dataSource, string subscription) =>
        new()
        {
            RecipientDestination = "ops@bank.example",
            RecipientNotificationType = NotificationType.Email,
            QueryResult = new QueryResult
            {
                QueryResults = "[]",
                TotalRecords = 1,
                DataSourceName = dataSource,
                SqlQuery = "SELECT 1",
                SubscriptionName = subscription,
                SubscriptionId = 5
            }
        };
}
