using Beacon.Core;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Jira;
using Beacon.Core.Adapters.Shared;
using Beacon.Core.Adapters.Webhook;
using Beacon.Core.Data.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Notifications;

[TestFixture]
public class OutboundLinkAndJiraCorrelationTests
{
    private const string Subscription = "Failed payments";

    [TestCase("https://beacon.example/app", "https://beacon.example/app/notifications/41")]
    [TestCase("http://evil.example", null)]
    [TestCase("javascript:alert(1)", null)]
    [TestCase("", null)]
    public void WebhookDefaultPayload_LinksOnlyThroughAUsableBaseUrl(string baseUrl, string? expected)
    {
        var adapter = new WebhookAdapter(null!, null!, new BeaconConfiguration { BaseUrl = baseUrl }, NullLogger<WebhookAdapter>.Instance);

        var payload = adapter.BuildDefaultPayload(Delivery(), null);

        if (expected == null)
        {
            payload.Should().NotContain("notificationUrl");
            return;
        }

        payload.Should().Contain($"\"notificationUrl\":\"{expected}\"");
    }

    [TestCase("https://beacon.example/app", "https://beacon.example/app/notifications/41")]
    [TestCase("http://evil.example", null)]
    public void TemplateData_LinksOnlyThroughAUsableBaseUrl(string baseUrl, string? expected)
    {
        var data = TemplateDataBuilder.BuildTemplateData(Delivery(), null, baseUrl);

        data["NotificationUrl"].Should().Be(expected);
    }

    [Test]
    public async Task LegacyFallback_IgnoresAnIssueLabelledForAnotherRecipient()
    {
        var client = new Mock<IJiraRestClient>();
        client
            .Setup(x => x.SearchAsync(It.Is<string>(y => y.Contains("labels =")), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found());
        client
            .Setup(x => x.SearchAsync(It.Is<string>(y => !y.Contains("labels =")), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(("SEC-1", ["Beacon", "beacon-sub-1-rcpt-6"])));
        client
            .Setup(x => x.CreateIssueAsync(It.IsAny<JiraCreateIssueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraCreateIssueResponse("9", "SEC-9", "self"));
        var adapter = new JiraAdapter(Adapter(client), NullLogger<JiraAdapter>.Instance);

        await adapter.SendNotificationAsync(Delivery(), lastNotificationResultCount: 1);

        client.Verify(x => x.AddCommentAsync("SEC-1", It.IsAny<JiraAddCommentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(x => x.GetTransitionsAsync("SEC-1", It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(x => x.TransitionIssueAsync("SEC-1", It.IsAny<JiraTransitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(x => x.AddCommentAsync("SEC-9", It.IsAny<JiraAddCommentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LegacyFallback_StillMatchesAnIssueWithNoCorrelationLabel()
    {
        var client = new Mock<IJiraRestClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found(("SEC-2", ["Beacon"])));
        var adapter = new JiraAdapter(Adapter(client), NullLogger<JiraAdapter>.Instance);

        await adapter.SendNotificationAsync(Delivery(), lastNotificationResultCount: 1);

        client.Verify(x => x.AddCommentAsync("SEC-2", It.IsAny<JiraAddCommentRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task CreateWorkItem_NeverReusesAnIssueCarryingAnotherCorrelationLabel()
    {
        var client = ClientFindingIssue("SEC-1", ["Beacon", "beacon-sub-1-rcpt-6"]);

        var result = await Adapter(client).CreateWorkItem(
            Credentials(), Subscription, $"BEACON: {Subscription}", new AdfDocument(), "Task", CancellationToken.None, "Beacon beacon-sub-1-rcpt-7");

        result.IsNewTicket.Should().BeTrue();
        client.Verify(x => x.CreateIssueAsync(It.IsAny<JiraCreateIssueRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(x => x.AddCommentAsync("SEC-1", It.IsAny<JiraAddCommentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase("beacon-sub-1-rcpt-7")]
    [TestCase(null)]
    public async Task CreateWorkItem_ReusesTheIssueOfItsOwnRecipientOrAnUnlabelledOne(string? existingLabel)
    {
        var client = ClientFindingIssue("SEC-1", existingLabel == null ? ["Beacon"] : ["Beacon", existingLabel]);

        var result = await Adapter(client).CreateWorkItem(
            Credentials(), Subscription, $"BEACON: {Subscription}", new AdfDocument(), "Task", CancellationToken.None, "Beacon beacon-sub-1-rcpt-7");

        result.IsNewTicket.Should().BeFalse();
        result.TicketKey.Should().Be("SEC-1");
    }

    [Test]
    public async Task CreateWorkItem_SearchesOnlyOpenIssues()
    {
        string? jql = null;
        var client = new Mock<IJiraRestClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, int, string?, CancellationToken>((y, _, _, _, _) => jql = y)
            .ReturnsAsync(new JiraSearchResponse(0, 10, 0, []));
        client
            .Setup(x => x.CreateIssueAsync(It.IsAny<JiraCreateIssueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraCreateIssueResponse("1", "SEC-3", "self"));

        await Adapter(client).CreateWorkItem(Credentials(), Subscription, $"BEACON: {Subscription}", new AdfDocument(), "Task", CancellationToken.None);

        jql.Should().Contain("statusCategory != Done");
    }

    private static Mock<IJiraRestClient> ClientFindingIssue(string key, string[] labels)
    {
        var fields = new JiraIssueFields($"BEACON: {Subscription}", null, new JiraStatus("1", "Open", null), null, null, null, null, null, null, labels);
        var client = new Mock<IJiraRestClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraSearchResponse(0, 10, 1, [new JiraIssueResponse("1", key, "self", fields)]));
        client
            .Setup(x => x.CreateIssueAsync(It.IsAny<JiraCreateIssueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraCreateIssueResponse("2", "SEC-NEW", "self"));

        return client;
    }

    private static JiraApiAdapter Adapter(Mock<IJiraRestClient> client)
    {
        var factory = new Mock<IJiraRestClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<JiraCredentials>())).Returns(client.Object);

        return new JiraApiAdapter(factory.Object, NullLogger<JiraApiAdapter>.Instance);
    }

    private static JiraCredentials Credentials() => new("acme;SEC;svc@acme.eu;token");

    private static JiraSearchResponse Found(params (string Key, string[] Labels)[] tickets)
    {
        var issues = tickets
            .Select(x =>
                new JiraIssueResponse(x.Key, x.Key, "self", new JiraIssueFields($"BEACON: {Subscription}", "", new JiraStatus("1", "Open", null), null, null, null, null, null, null, x.Labels)))
            .ToArray();

        return new JiraSearchResponse(0, 50, issues.Length, issues);
    }

    private static RecipientQueryResult Delivery()
    {
        return new RecipientQueryResult
        {
            RecipientDestination = "acme;SEC;svc@acme.eu;token",
            RecipientNotificationType = NotificationType.Jira,
            NotificationId = 41,
            RecipientId = 7,
            QueryResult = new QueryResult
            {
                QueryResults = "",
                TotalRecords = 3,
                DataSourceName = "source",
                SqlQuery = "SELECT 1",
                SubscriptionName = Subscription,
                SubscriptionId = 1
            }
        };
    }
}
