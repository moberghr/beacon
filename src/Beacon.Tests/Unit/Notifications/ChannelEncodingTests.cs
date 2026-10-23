using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Beacon.Core;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Mail;
using Beacon.Core.Adapters.Shared;
using Beacon.Core.Adapters.Slack;
using Beacon.Core.Adapters.Teams;
using Beacon.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers.File;
using Beacon.Core.Models;
using Beacon.Core.Models.Anomaly;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// Values from rows, names, SQL and anomaly text are encoded for the channel they are shown in, kept within the
/// channel's size limits, and stripped of direction and zero-width characters; exported cells never read as formulas.
/// </summary>
[TestFixture]
[SetCulture("")]
public class ChannelEncodingTests
{
    private const string Mention = "<!channel> <@U123> <https://x.example|Reset your password>";

    [Test]
    public void SlackText_EscapesTheThreeControlCharacters()
    {
        ChannelText.Slack(Mention).Should().Be("&lt;!channel&gt; &lt;@U123&gt; &lt;https://x.example|Reset your password&gt;");
        ChannelText.Slack("a & b").Should().Be("a &amp; b");
    }

    [Test]
    public void SlackFencedText_CannotCloseTheFence()
    {
        ChannelText.SlackFenced("x```\n<!here>").Should().NotContain("`").And.NotContain("<");
    }

    [Test]
    public void StripUnsafe_RemovesDirectionZeroWidthAndControlCharacters_KeepsLineBreaks()
    {
        ChannelText.StripUnsafe("a‮b​c⁦d\u0007e\tf\ng").Should().Be("abcde\tf\ng");
    }

    [Test]
    public void SlackMessage_ValuesFromRowsNamesSqlAndAnomalyAreEscaped()
    {
        var result = Result(
            [Row(("<!everyone>", Mention), ("Id", 1))],
            dataSource: "ds <!channel>",
            sql: "SELECT '<@U1>' ``` x");
        var anomaly = new AnomalyEvaluationResult { IsAnomaly = true, Explanation = Mention, Severity = "High" };

        var message = new SlackMessageBuilder(new SlackTableFormatter(), new BeaconConfiguration()).BuildNotificationMessage(result, 4, anomaly);
        var json = JsonSerializer.Serialize(message);
        var text = JsonDocument.Parse(json).RootElement.GetProperty("blocks").EnumerateArray()
            .Select(x => x.TryGetProperty("text", out var t) && t.TryGetProperty("text", out var inner) ? inner.GetString() : null)
            .Where(x => x != null)
            .ToList();

        text.Should().NotBeEmpty();
        message.Text.Should().NotContain("<").And.Contain("&lt;!channel&gt;");
        text.Where(x => !x!.StartsWith("[Beacon]") && !x.Contains("ANOMALY")).Should().OnlyContain(x => !x!.Contains("<!") && !x.Contains("<@") && !x.Contains("<https"));
        text.Count(x => x!.Contains("&lt;!channel&gt;")).Should().BeGreaterThan(0);
        text.Single(x => x!.StartsWith("*Query:*")).Should().Contain("&lt;@U1&gt;").And.NotContain("``` x");
    }

    [Test]
    public void SlackMessage_LongSqlAndRows_StayUnderTheSectionLimit_AndSayTheyWereCut()
    {
        var rows = Enumerable.Range(0, 25).Select(x => Row(("c" + x % 5, new string('<', 30)), ("d", new string('&', 30)))).ToList();
        var result = Result(rows, sql: new string('x', 10_000));

        var message = new SlackMessageBuilder(new SlackTableFormatter(), new BeaconConfiguration()).BuildNotificationMessage(result, 4);
        var sections = JsonDocument.Parse(JsonSerializer.Serialize(message)).RootElement.GetProperty("blocks").EnumerateArray()
            .Select(x => x.TryGetProperty("text", out var t) && t.TryGetProperty("text", out var inner) ? inner.GetString()! : null)
            .Where(x => x != null)
            .ToList();

        sections.Should().OnlyContain(x => x!.Length <= 3000);
        sections.Single(x => x!.StartsWith("*Query:*")).Should().Contain("(truncated)");
        sections.Single(x => x!.StartsWith("```\n")).Should().Contain("(truncated)");
    }

    [Test]
    public void SlackMessage_LongNames_HeaderFitsSlacksLimit()
    {
        var result = Result([], dataSource: new string('d', 200), subscription: new string('s', 200));

        var message = new SlackMessageBuilder(new SlackTableFormatter(), new BeaconConfiguration()).BuildNotificationMessage(result, null);
        var header = JsonDocument.Parse(JsonSerializer.Serialize(message)).RootElement.GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString()!;

        header.Length.Should().BeLessThanOrEqualTo(150);
    }

    [Test]
    public void Template_SlackMode_EscapesValuesAndEveryStringInRecords()
    {
        var data = new Dictionary<string, object?>
        {
            ["SubscriptionName"] = Mention,
            ["Records"] = new List<IDictionary<string, object?>> { Row(("<!channel>", Mention)) },
            ["Anomaly"] = new Dictionary<string, object?> { ["Explanation"] = Mention }
        };

        var result = AdapterTemplateProcessor.ProcessTemplate(
            "{\"a\":\"{{SubscriptionName}}\",\"r\":{{Records}},\"x\":\"{{Anomaly.Explanation}}\"}",
            data,
            escapeJsonStrings: true,
            escapeText: ChannelText.Slack);

        using var document = JsonDocument.Parse(result);
        var decoded = document.RootElement.GetRawText();
        document.RootElement.GetProperty("a").GetString().Should().NotContain("<");
        document.RootElement.GetProperty("x").GetString().Should().NotContain("<");
        var record = document.RootElement.GetProperty("r")[0];
        record.EnumerateObject().Single().Name.Should().Be("&lt;!channel&gt;");
        record.EnumerateObject().Single().Value.GetString().Should().NotContain("<").And.Contain("&lt;!channel&gt;");
        decoded.Should().NotContain("<!channel>");
        ((List<IDictionary<string, object?>>)data["Records"]!)[0].Values.Single().Should().Be(Mention);
    }

    [Test]
    public void Template_TeamsMode_EscapesMarkdown()
    {
        var data = new Dictionary<string, object?> { ["SubscriptionName"] = "[click](https://x.example) *b* <at>x</at>" };

        var result = AdapterTemplateProcessor.ProcessTemplate("{\"t\":\"{{SubscriptionName}}\"}", data, escapeJsonStrings: true, escapeText: ChannelText.Teams);

        JsonDocument.Parse(result).RootElement.GetProperty("t").GetString().Should().Be("\\[click\\](https://x.example) \\*b\\* \\<at>x\\</at>");
    }

    [Test]
    public void Template_UnknownPlaceholderEcho_StaysInsideTheJsonString()
    {
        var result = AdapterTemplateProcessor.ProcessTemplate("{\"t\":\"{{a\"b}}\"}", new Dictionary<string, object?>(), escapeJsonStrings: true);

        JsonDocument.Parse(result).RootElement.GetProperty("t").GetString().Should().Be("{{UNKNOWN:a\"b}}");
    }

    [Test]
    public async Task TeamsCard_EscapesMarkdownInValues_AndKeepsItUnderTheCardLimit()
    {
        var rows = Enumerable.Range(0, 200).Select(x => Row(("Name", "[a](https://x.example) " + new string('z', 190)), ("N", x))).ToList();
        var card = await PostedTeamsCard(Result(rows, subscription: "*bold* [l](u)", sql: new string('q', 5000)));

        Encoding.UTF8.GetByteCount(card).Should().BeLessThanOrEqualTo(24_000);
        var body = JsonDocument.Parse(card).RootElement.GetProperty("body");
        body.EnumerateArray().Single(x => x.TryGetProperty("id", out var id) && id.GetString() == "subtitle").GetProperty("text").GetString()
            .Should().Be("warehouse: \\*bold\\* \\[l\\](u)");
        body.EnumerateArray().Single(x => x.TryGetProperty("id", out var id) && id.GetString() == "queryText").GetProperty("text").GetString()
            .Should().Contain("(truncated)");
        JsonDocument.Parse(card).RootElement.GetProperty("body").EnumerateArray()
            .Any(x => x.TryGetProperty("id", out var id) && id.GetString() == "truncatedText").Should().BeTrue();
    }

    [Test]
    public async Task EmailSubject_NeverSplitsASurrogatePair_AndDropsFormatCharacters()
    {
        string? subject = null;
        var mail = new Mock<IEmailAdapter>();
        mail.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryResultFile?>()))
            .Callback<string, string, string, QueryResultFile?>((_, s, _, _) => subject = s)
            .Returns(Task.CompletedTask);
        var padding = new string('a', 200 - "[Beacon] ".Length - 1);

        await new EmailAdapter(mail.Object).SendNotificationAsync(Recipient(padding + "\U0001F600tail", "x‮y​"), null);

        subject.Should().NotBeNull();
        subject!.Length.Should().BeLessThanOrEqualTo(200);
        subject.Should().NotContainAny("‮", "​");
        char.IsHighSurrogate(subject[^1]).Should().BeFalse();
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(subject)).Should().Be(subject);
    }

    [Test]
    public void EmailBody_ValuesAreStrippedOfDirectionOverrides()
    {
        var result = Result([Row(("c‮", "a‮b"))]);

        var html = Helpers.GenerateEmailContent(result);

        html.Should().NotContain("‮");
    }

    [TestCase("=1+1")]
    [TestCase("+1")]
    [TestCase("-1")]
    [TestCase("@SUM(A1)")]
    [TestCase("\tx")]
    [TestCase("\rx")]
    public async Task Csv_StringCellThatStartsLikeAFormula_GetsAQuotePrefix(string value)
    {
        var text = Encoding.UTF8.GetString(await CsvBuilder.GetReport([Row(("c", value), ("=h", "ok"))])).TrimStart('﻿');

        text.Should().Contain("'" + value.TrimEnd()).And.Contain("'=h");
    }

    [Test]
    public async Task Csv_NumbersDatesAndOrdinaryText_AreUntouched()
    {
        var text = Encoding.UTF8.GetString(await CsvBuilder.GetReport([Row(("n", -5), ("d", 2.5m), ("t", "hello-world"), ("e", "a=b"))])).TrimStart('﻿');

        text.Should().Contain("-5,2.5,hello-world,a=b");
    }

    [Test]
    public async Task Csv_WithNeutralisationOff_WritesTheTextAsItIs()
    {
        var text = Encoding.UTF8.GetString(await CsvBuilder.GetReport([Row(("c", "=1+1"))], ["c"], CancellationToken.None, neutralizeFormulas: false)).TrimStart('﻿');

        text.Should().Contain("=1+1").And.NotContain("'=1+1");
    }

    [Test]
    public async Task Xlsx_StringCellThatStartsLikeAFormula_IsAQuotePrefixedTextCell()
    {
        var bytes = await XlsxBuilder.GetReport([Row(("c", "=1+1"), ("n", -5), ("t", "plain"))]);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet(1);
        sheet.Cell(2, 1).HasFormula.Should().BeFalse();
        sheet.Cell(2, 1).GetString().Should().Be("=1+1");
        sheet.Cell(2, 1).Style.IncludeQuotePrefix.Should().BeTrue();
        sheet.Cell(2, 2).Style.IncludeQuotePrefix.Should().BeFalse();
        sheet.Cell(2, 2).Value.IsNumber.Should().BeTrue();
        sheet.Cell(2, 3).Style.IncludeQuotePrefix.Should().BeFalse();
    }

    [Test]
    public async Task ExportProvider_NeutralisationFollowsTheSwitch()
    {
        var rows = new List<IDictionary<string, object?>> { Row(("c", "@x")) };

        var on = await ExportProvider.GetReport(FileType.Csv, rows, 1_000_000, CancellationToken.None);
        var off = await ExportProvider.GetReport(FileType.Csv, rows, 1_000_000, CancellationToken.None, neutralizeFormulas: false);

        Encoding.UTF8.GetString(on.Data).Should().Contain("'@x");
        Encoding.UTF8.GetString(off.Data).Should().NotContain("'@x");
    }

    private static async Task<string> PostedTeamsCard(QueryResult result)
    {
        string? body = null;
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new Capture(x => body = x), disposeHandler: false));
        var sender = new NotificationHttpSender(factory.Object, NullLogger<NotificationHttpSender>.Instance);
        var adapter = new TeamsAdapter(sender, new BeaconConfiguration(), NullLogger<TeamsAdapter>.Instance);

        await adapter.SendNotificationAsync(
            new RecipientQueryResult
            {
                RecipientDestination = "https://example.webhook.office.com/webhook",
                RecipientNotificationType = NotificationType.Teams,
                QueryResult = result
            },
            lastNotificationResultCount: null);

        return body!;
    }

    private sealed class Capture(Action<string> store) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            store(await request.Content!.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private static IDictionary<string, object?> Row(params (string Key, object? Value)[] cells)
    {
        return cells.ToDictionary(x => x.Key, x => x.Value);
    }

    private static QueryResult Result(List<IDictionary<string, object?>> rows, string dataSource = "warehouse", string subscription = "daily", string sql = "SELECT 1")
    {
        return new QueryResult
        {
            QueryResults = "[]",
            TotalRecords = rows.Count,
            DataSourceName = dataSource,
            SqlQuery = sql,
            SubscriptionName = subscription,
            SubscriptionId = 5,
            ShowQuery = true,
            TopRecords = rows
        };
    }

    private static RecipientQueryResult Recipient(string dataSource, string subscription)
    {
        return new RecipientQueryResult
        {
            RecipientDestination = "ops@bank.example",
            RecipientNotificationType = NotificationType.Email,
            QueryResult = Result([], dataSource, subscription)
        };
    }
}
