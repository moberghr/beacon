using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Jira;
using Beacon.Core.Data.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// A subscription's Jira deliveries go to one issue: the first creates it as "BEACON: {name}", later ones find it by that
/// same summary, comment on it while there are rows and move it to Done when there are none. An issue whose summary is the
/// bare subscription name is someone else's and is not reused.
/// </summary>
[TestFixture]
public class JiraIssueReuseTests
{
    private const string Subscription = "Failed payments";
    private const string Recipient = "acme;SEC;svc@acme.eu;token";

    private Mock<IJiraRestClient> _jira = null!;

    [SetUp]
    public void SetUp()
    {
        _jira = new Mock<IJiraRestClient>();
        _jira
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found());
        _jira
            .Setup(x => x.CreateIssueAsync(It.IsAny<JiraCreateIssueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraCreateIssueResponse("99", "SEC-99", "self"));
        _jira
            .Setup(x => x.GetTransitionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraTransitionsResponse([new JiraTransition("31", "Done", null)]));
    }

    [Test]
    public async Task FirstDelivery_CreatesTheIssue_WithTheBeaconSummary()
    {
        await Adapter().SendNotificationAsync(Delivery(totalRecords: 3), lastNotificationResultCount: null);

        VerifyCreated(Times.Once());
        VerifyCommented("SEC-99", Times.Once());
    }

    [Test]
    public async Task LaterDeliveryWithRows_CommentsOnTheIssueItCreated()
    {
        Existing(new Ticket("SEC-1", $"BEACON: {Subscription}"));

        await Adapter().SendNotificationAsync(Delivery(totalRecords: 3), lastNotificationResultCount: 1);

        VerifyCreated(Times.Never());
        VerifyCommented("SEC-1", Times.Once());
    }

    [Test]
    public async Task LaterDeliveryWithoutRows_MovesTheIssueItCreatedToDone()
    {
        Existing(new Ticket("SEC-1", $"BEACON: {Subscription}"));

        await Adapter().SendNotificationAsync(Delivery(totalRecords: 0), lastNotificationResultCount: 2);

        _jira.Verify(x => x.TransitionIssueAsync("SEC-1", It.Is<JiraTransitionRequest>(y => y.Transition.Id == "31"), It.IsAny<CancellationToken>()), Times.Once);
        VerifyCreated(Times.Never());
        _jira.Verify(x => x.AddCommentAsync(It.IsAny<string>(), It.IsAny<JiraAddCommentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AnIssueNamedLikeTheSubscriptionButNotCreatedByIt_IsNotReused()
    {
        Existing(new Ticket("OTHER-7", Subscription));

        await Adapter().SendNotificationAsync(Delivery(totalRecords: 0), lastNotificationResultCount: 2);
        await Adapter().SendNotificationAsync(Delivery(totalRecords: 3), lastNotificationResultCount: 2);

        _jira.Verify(x => x.TransitionIssueAsync("OTHER-7", It.IsAny<JiraTransitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyCommented("OTHER-7", Times.Never());
        VerifyCreated(Times.Once());
    }

    // The ticket an issue search answers with: only the legacy (text) search finds it unless labelled is set.
    private void Existing(Ticket ticket, bool labelled = false)
    {
        var found = Found(ticket);
        _jira
            .Setup(x => x.SearchAsync(It.Is<string>(y => y.Contains("labels =") == labelled), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(found);
        _jira
            .Setup(x => x.SearchAsync(It.Is<string>(y => y.Contains("labels =") != labelled), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found());
    }

    [Test]
    public async Task FirstDelivery_LabelsTheIssueWithTheSubscriptionAndRecipient()
    {
        await Adapter().SendNotificationAsync(Delivery(totalRecords: 3), lastNotificationResultCount: null);

        _jira.Verify(
            x => x.CreateIssueAsync(
                It.Is<JiraCreateIssueRequest>(y => y.Fields.IssueType.Name == "Task" && y.Fields.Labels!.SequenceEqual(new[] { "Beacon", "beacon-sub-1-rcpt-7" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task LaterDelivery_FindsTheIssueByLabelEvenWhenTheSummaryWasEdited()
    {
        Existing(new Ticket("SEC-5", "renamed by a person"), labelled: true);

        await Adapter().SendNotificationAsync(Delivery(totalRecords: 3), lastNotificationResultCount: 1);

        VerifyCreated(Times.Never());
        VerifyCommented("SEC-5", Times.Once());
    }

    [Test]
    public async Task LaterDelivery_SearchesOnlyTheRecipientsProjectForOpenIssues()
    {
        // The ticket searches (they ask for the description); the search a create makes for its own issue is not one.
        var queries = new List<string>();
        _jira
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, int, string?, CancellationToken>((jql, _, _, fields, _) =>
            {
                if (fields!.Contains("description"))
                {
                    queries.Add(jql);
                }
            })
            .ReturnsAsync(Found());

        await Adapter().SendNotificationAsync(Delivery(totalRecords: 3), lastNotificationResultCount: 1);

        queries.Should().HaveCount(2);
        queries.Should().OnlyContain(x => x.Contains("project = \"SEC\"") && x.Contains("statusCategory != Done"));
        queries[0].Should().Contain("labels = \"beacon-sub-1-rcpt-7\"");
    }

    private void VerifyCreated(Times times)
    {
        _jira.Verify(
            x => x.CreateIssueAsync(
                It.Is<JiraCreateIssueRequest>(y => y.Fields.Project.Key == "SEC" && y.Fields.Summary == $"BEACON: {Subscription}" && y.Fields.IssueType.Name == "Task"),
                It.IsAny<CancellationToken>()),
            times);
    }

    private void VerifyCommented(string key, Times times)
    {
        _jira.Verify(x => x.AddCommentAsync(key, It.IsAny<JiraAddCommentRequest>(), It.IsAny<CancellationToken>()), times);
    }

    private static JiraSearchResponse Found(params Ticket[] tickets)
    {
        var issues = tickets
            .Select(x =>
                new JiraIssueResponse(x.Key, x.Key, "self", new JiraIssueFields(x.Summary, "", new JiraStatus("1", "Open", null), null, null, null, null, null, null)))
            .ToArray();

        return new JiraSearchResponse(0, 50, issues.Length, issues);
    }

    private IAdapter Adapter()
    {
        var factory = new Mock<IJiraRestClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<JiraCredentials>())).Returns(_jira.Object);

        return new JiraAdapter(new JiraApiAdapter(factory.Object, NullLogger<JiraApiAdapter>.Instance), NullLogger<JiraAdapter>.Instance);
    }

    private static RecipientQueryResult Delivery(int totalRecords)
    {
        return new RecipientQueryResult
        {
            RecipientDestination = Recipient,
            RecipientNotificationType = NotificationType.Jira,
            QueryResult = new QueryResult
            {
                QueryResults = "",
                TotalRecords = totalRecords,
                DataSourceName = "source",
                SqlQuery = "SELECT 1",
                SubscriptionName = Subscription,
                SubscriptionId = 1
            },
            RecipientId = 7
        };
    }

    private sealed record Ticket(string Key, string Summary);
}
