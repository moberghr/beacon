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

    private Mock<IJiraApiAdapter> _jira = null!;

    [SetUp]
    public void SetUp()
    {
        _jira = new Mock<IJiraApiAdapter>();
        _jira
            .Setup(x => x.CreateWorkItem(
                It.IsAny<JiraCredentials>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AdfDocument>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new CreateWorkItemResult("SEC-99", "https://acme.atlassian.net/browse/SEC-99", true));
    }

    [Test]
    public async Task FirstDelivery_CreatesTheIssue_WithTheBeaconSummary()
    {
        await Adapter().SendNotificationAsync(Delivery(totalRecords: 3), lastNotificationResultCount: null);

        VerifyCreated(Times.Once());
        _jira.Verify(x => x.AddCommentToTicket(It.IsAny<JiraCredentials>(), "SEC-99", It.IsAny<AdfDocument>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LaterDeliveryWithRows_CommentsOnTheIssueItCreated()
    {
        Existing(new Ticket("SEC-1", $"BEACON: {Subscription}"));

        await Adapter().SendNotificationAsync(Delivery(totalRecords: 3), lastNotificationResultCount: 1);

        VerifyCreated(Times.Never());
        _jira.Verify(x => x.AddCommentToTicket(It.IsAny<JiraCredentials>(), "SEC-1", It.IsAny<AdfDocument>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LaterDeliveryWithoutRows_MovesTheIssueItCreatedToDone()
    {
        Existing(new Ticket("SEC-1", $"BEACON: {Subscription}"));

        await Adapter().SendNotificationAsync(Delivery(totalRecords: 0), lastNotificationResultCount: 2);

        _jira.Verify(x => x.TransitionIssue(It.IsAny<JiraCredentials>(), "SEC-1", "Done", It.IsAny<CancellationToken>()), Times.Once);
        VerifyCreated(Times.Never());
        _jira.Verify(x => x.AddCommentToTicket(It.IsAny<JiraCredentials>(), It.IsAny<string>(), It.IsAny<AdfDocument>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AnIssueNamedLikeTheSubscriptionButNotCreatedByIt_IsNotReused()
    {
        Existing(new Ticket("OTHER-7", Subscription));

        await Adapter().SendNotificationAsync(Delivery(totalRecords: 0), lastNotificationResultCount: 2);
        await Adapter().SendNotificationAsync(Delivery(totalRecords: 3), lastNotificationResultCount: 2);

        _jira.Verify(x => x.TransitionIssue(It.IsAny<JiraCredentials>(), "OTHER-7", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _jira.Verify(x => x.AddCommentToTicket(It.IsAny<JiraCredentials>(), "OTHER-7", It.IsAny<AdfDocument>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyCreated(Times.Once());
    }

    private void Existing(Ticket ticket)
    {
        _jira
            .Setup(x => x.SearchTickets(It.IsAny<JiraCredentials>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new JiraTicketSearchDto(ticket.Key, ticket.Summary, "", "Open", null, DateTime.UtcNow, DateTime.UtcNow)]);
    }

    private void VerifyCreated(Times times)
    {
        _jira.Verify(
            x => x.CreateWorkItem(
                It.IsAny<JiraCredentials>(), Subscription, $"BEACON: {Subscription}", It.IsAny<AdfDocument>(), "Task",
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            times);
    }

    private IAdapter Adapter() => new JiraAdapter(_jira.Object, NullLogger<JiraAdapter>.Instance);

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
            }
        };
    }

    private sealed record Ticket(string Key, string Summary);
}
