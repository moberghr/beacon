using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Refit;
using Beacon.Core;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Jira;
using Beacon.Core.Adapters.Slack;
using Beacon.Core.Adapters.Teams;
using Beacon.Core.Adapters.Webhook;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Anomaly;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Core.Worker.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// Delivery: the stored destination is decrypted and re-checked against the policy before any adapter runs; every
/// failure is logged at Warning with ids, status and exception types but never a message, URL or body, and leaves with a
/// generic reason, which is all the execution history ever stores. A run tries every recipient and keeps a Notification
/// only for those that were notified.
/// </summary>
[TestFixture]
public class NotificationDeliveryTests
{
    private const string RemoteBody = "{\"error\":\"index [customers] token=abc123\"}";
    private const string RemoteToken = "abc123";

    [Test]
    public async Task Send_DecryptsTheStoredDestinationAndHeadersForTheAdapter()
    {
        var protector = NotificationTestKit.Protector();
        var adapter = new RecordingAdapter(NotificationType.Webhook);

        await Service(adapter).SendNotification(
            Delivery(NotificationType.Webhook, protector.Protect("https://hooks.example.com/in"), protector.Protect("{\"Authorization\":\"Bearer t\"}")),
            null,
            CancellationToken.None);

        adapter.Received.Should().ContainSingle();
        adapter.Received[0].RecipientDestination.Should().Be("https://hooks.example.com/in");
        adapter.Received[0].HeadersJson.Should().Be("{\"Authorization\":\"Bearer t\"}");
    }

    [Test]
    public async Task Send_Email_GoesOutAsTheNormalisedAddressList()
    {
        var adapter = new RecordingAdapter(NotificationType.Email);

        await Service(adapter).SendNotification(Delivery(NotificationType.Email, NotificationTestKit.Protector().Protect("ops@example.com; risk@example.org")), null, CancellationToken.None);

        adapter.Received.Single().RecipientDestination.Should().Be("ops@example.com,risk@example.org");
    }

    [TestCase(NotificationType.Webhook, "http://10.0.0.5:8080/admin")]
    [TestCase(NotificationType.Webhook, "https://169.254.169.254/latest/meta-data")]
    [TestCase(NotificationType.Slack, "https://collector.example.net/services/x")]
    [TestCase(NotificationType.Jira, "x@unlisted.example:443/;SEC;svc@acme.eu;token")]
    [TestCase(NotificationType.Email, "Ops <ops@example.com>")]
    public async Task Send_ToAStoredDestinationThePolicyRefuses_NeverReachesTheAdapter(NotificationType type, string legacyDestination)
    {
        var adapter = new RecordingAdapter(type);

        var act = () => Service(adapter).SendNotification(Delivery(type, legacyDestination), null, CancellationToken.None);

        await act.Should().ThrowAsync<NotificationDeliveryException>().WithMessage(NotificationFailureReasons.DestinationNotAllowed);
        adapter.Received.Should().BeEmpty();
    }

    [Test]
    public async Task Send_WithStoredHeadersOffTheAllowList_NeverReachesTheAdapter()
    {
        var adapter = new RecordingAdapter(NotificationType.Webhook);

        var act = () => Service(adapter).SendNotification(
            Delivery(NotificationType.Webhook, "https://hooks.example.com/in", "{\"X-Team\":\"ops\"}"),
            null,
            CancellationToken.None);

        await act.Should().ThrowAsync<NotificationDeliveryException>().WithMessage(NotificationFailureReasons.DestinationNotAllowed);
        adapter.Received.Should().BeEmpty();
    }

    [Test]
    public async Task Send_OfADisabledType_NeverReachesTheAdapter()
    {
        var adapter = new RecordingAdapter(NotificationType.Webhook);

        var act = () => Service(adapter, new NotificationChannelOptions { DisabledTypes = [NotificationType.Webhook] })
            .SendNotification(Delivery(NotificationType.Webhook, "https://hooks.example.com/in"), null, CancellationToken.None);

        await act.Should().ThrowAsync<NotificationDeliveryException>().WithMessage(NotificationFailureReasons.DestinationNotAllowed);
        adapter.Received.Should().BeEmpty();
    }

    [Test]
    public async Task Send_WithADestinationThatCannotBeDecrypted_FailsGenericallyAndLogsAtWarning()
    {
        var foreign = new RecipientSecretProtector(new EncryptionService("another-key")).Protect("https://hooks.example.com/in");
        var adapter = new RecordingAdapter(NotificationType.Webhook);
        var logger = new CapturingLogger<NotificationService>();

        var act = () => Service(adapter, logger: logger).SendNotification(Delivery(NotificationType.Webhook, foreign, notificationId: 41, recipientId: 9), null, CancellationToken.None);

        await act.Should().ThrowAsync<NotificationDeliveryException>().WithMessage(NotificationFailureReasons.DestinationUnreadable);
        adapter.Received.Should().BeEmpty();
        logger.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains(" failed: "))
            .Which.Message.Should().Contain("41").And.Contain("9").And.Contain(NotificationFailureReasons.DestinationUnreadable);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Send_PlaintextStoredSecrets_AreRefusedOnlyWhenEncryptionIsRequired(bool required)
    {
        var adapter = new RecordingAdapter(NotificationType.Webhook);
        var options = new NotificationChannelOptions { RequireEncryptedSecrets = required };

        var act = () => Service(adapter, options).SendNotification(Delivery(NotificationType.Webhook, "https://hooks.example.com/in"), null, CancellationToken.None);

        if (required)
        {
            await act.Should().ThrowAsync<NotificationDeliveryException>().WithMessage(NotificationFailureReasons.DestinationNotEncrypted);
            adapter.Received.Should().BeEmpty();
        }
        else
        {
            await act.Should().NotThrowAsync();
            adapter.Received.Should().ContainSingle();
        }
    }

    [Test]
    public async Task Send_EncryptedDestinationWithPlaintextHeaders_IsRefusedWhenEncryptionIsRequired()
    {
        var adapter = new RecordingAdapter(NotificationType.Webhook);
        var options = new NotificationChannelOptions { RequireEncryptedSecrets = true };
        var delivery = Delivery(NotificationType.Webhook, NotificationTestKit.Protector().Protect("https://hooks.example.com/in"), "{\"Authorization\":\"Bearer t\"}");

        var act = () => Service(adapter, options).SendNotification(delivery, null, CancellationToken.None);

        await act.Should().ThrowAsync<NotificationDeliveryException>().WithMessage(NotificationFailureReasons.DestinationNotEncrypted);
    }

    private static IEnumerable<TestCaseData> AdapterFailures()
    {
        yield return new TestCaseData(new BeaconException("Failed to send: 500. " + RemoteBody), NotificationFailureReasons.Generic, null).SetName("RemoteTextInAMessage");
        yield return new TestCaseData(new HttpRequestException("Connection refused (10.0.0.5:8080)"), NotificationFailureReasons.ConnectionFailed, null).SetName("ConnectionRefused");
        yield return new TestCaseData(new HttpRequestException("blocked", new OutboundConnectionBlockedException()), NotificationFailureReasons.ConnectionBlocked, null).SetName("BlockedByPolicy");
        yield return new TestCaseData(new HttpRequestException("status", null, HttpStatusCode.BadGateway), "Notification delivery failed: the destination returned HTTP 5xx.", 502).SetName("HttpStatusOnRequestException");
        yield return new TestCaseData(new TaskCanceledException("timeout", new TimeoutException()), NotificationFailureReasons.TimedOut, null).SetName("TimedOut");
        yield return new TestCaseData(NotificationDeliveryException.ForStatus(HttpStatusCode.Forbidden), "Notification delivery failed: the destination returned HTTP 4xx.", 403).SetName("StatusFromTheSender");
        yield return new TestCaseData(new InvalidOperationException("SMTP 550 mailbox relay.bank.internal unavailable"), NotificationFailureReasons.Generic, null).SetName("HostEmailAdapterText");
    }

    [TestCaseSource(nameof(AdapterFailures))]
    public async Task Send_AdapterFailure_LeavesAGenericReasonAndLogsNoRemoteText(Exception thrown, string expected, int? status)
    {
        var adapter = new RecordingAdapter(NotificationType.Webhook) { Failure = thrown };
        var logger = new CapturingLogger<NotificationService>();

        var act = () => Service(adapter, logger: logger).SendNotification(
            Delivery(NotificationType.Webhook, "https://hooks.example.com/in/PATHSECRET", notificationId: 41, recipientId: 9),
            null,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<NotificationDeliveryException>();
        failure.Which.Message.Should().Be(expected);
        failure.Which.InnerException.Should().BeNull("an error-level log of the exception must not carry the remote text");
        ((int?)failure.Which.StatusCode).Should().Be(status);

        // (The once-per-process warning about plaintext rows may also be in this log.)
        var warning = logger.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains(" failed: ")).Subject;
        warning.Message.Should().Contain(expected).And.Contain(thrown.GetType().Name).And.Contain("41").And.Contain("9");
        warning.Exception.Should().BeNull();
        logger.TextAtOrAbove(LogLevel.Trace).Should().NotContain(RemoteToken).And.NotContain("10.0.0.5").And.NotContain("relay.bank.internal").And.NotContain("PATHSECRET");
    }

    [Test]
    public async Task Send_CancelledByTheCaller_StaysACancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var adapter = new RecordingAdapter(NotificationType.Webhook) { Failure = new OperationCanceledException(cancellation.Token) };

        var act = () => Service(adapter).SendNotification(Delivery(NotificationType.Webhook, "https://hooks.example.com/in"), null, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestCase(HttpStatusCode.BadRequest, "Notification delivery failed: the destination returned HTTP 4xx.")]
    [TestCase(HttpStatusCode.Found, "Notification delivery failed: the destination returned HTTP 3xx.")]
    [TestCase(HttpStatusCode.ServiceUnavailable, "Notification delivery failed: the destination returned HTTP 5xx.")]
    [TestCase((HttpStatusCode)99, NotificationFailureReasons.Generic)]
    public void ForStatus_KeepsTheStatusClassOnly(HttpStatusCode statusCode, string expected)
    {
        NotificationFailureReasons.ForStatus(statusCode).Should().Be(expected);
    }

    // --- adapters end to end against a loopback server that answers 500 with a body ----------------------------------

    [TestCase(NotificationType.Webhook)]
    [TestCase(NotificationType.Slack)]
    [TestCase(NotificationType.Teams)]
    public async Task Adapter_ErrorResponse_CarriesTheStatusAndNeverTheBody(NotificationType type)
    {
        using var server = LoopbackServer.Start($"HTTP/1.1 500 Internal Server Error\r\nContent-Type: application/json\r\nContent-Length: {RemoteBody.Length}\r\n\r\n{RemoteBody}");
        var senderLogger = new CapturingLogger<NotificationHttpSender>();
        using var provider = HttpServices(type, senderLogger);
        var adapter = Adapter(type, provider);

        var act = () => adapter.SendNotificationAsync(
            new RecipientQueryResult { RecipientDestination = server.Url, RecipientNotificationType = type, QueryResult = Result() },
            null,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<NotificationDeliveryException>();
        failure.Which.Message.Should().Be("Notification delivery failed: the destination returned HTTP 5xx.");
        ((int?)failure.Which.StatusCode).Should().Be(500);
        senderLogger.TextAtOrAbove(LogLevel.Trace).Should().NotContain(RemoteToken).And.NotContain(server.Url);
        senderLogger.Entries.Should().OnlyContain(x => x.Level == LogLevel.Debug);
    }

    [Test]
    public async Task Sender_StalledDestination_TimesOut()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/in";
        using var provider = HttpServices(NotificationType.Webhook, new CapturingLogger<NotificationHttpSender>());
        var handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(x => x.CreateClient(NotificationHttpClient.NameFor(NotificationType.Webhook)))
            .Returns(() => new HttpClient(handlers.CreateHandler(NotificationHttpClient.NameFor(NotificationType.Webhook)), disposeHandler: false) { Timeout = TimeSpan.FromMilliseconds(300) });
        var sender = new NotificationHttpSender(factory.Object, NullLogger<NotificationHttpSender>.Instance);

        var act = () => sender.PostAsync(NotificationType.Webhook, url, new StringContent("{}"), headers: null, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<TaskCanceledException>();
        NotificationFailureReasons.For(thrown.Which).Should().Be(NotificationFailureReasons.TimedOut);
    }

    // --- Jira ----------------------------------------------------------------------------------------------------

    [Test]
    public async Task JiraCreateIssueFailure_CarriesTheStatusNotTheResponse()
    {
        var client = new Mock<IJiraRestClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiraSearchResponse(0, 10, 0, []));
        client
            .Setup(x => x.CreateIssueAsync(It.IsAny<JiraCreateIssueRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(await JiraError(HttpStatusCode.BadRequest));
        var logger = new CapturingLogger<JiraApiAdapter>();

        var act = () => JiraAdapter(client, logger).CreateWorkItem(Credentials(), "session", "title", new AdfDocument(), "Task", CancellationToken.None);

        var failure = await act.Should().ThrowAsync<NotificationDeliveryException>();
        failure.Which.Message.Should().Be("Notification delivery failed: the destination returned HTTP 4xx.");
        ((int?)failure.Which.StatusCode).Should().Be(400);
        failure.Which.InnerException.Should().BeNull();
        logger.TextAtOrAbove(LogLevel.Trace).Should().NotContain(RemoteToken);
    }

    [Test]
    public async Task JiraOtherCalls_FailuresMapToTheStatusClass()
    {
        var client = new Mock<IJiraRestClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(await JiraError(HttpStatusCode.Unauthorized));
        client
            .Setup(x => x.AddCommentAsync(It.IsAny<string>(), It.IsAny<JiraAddCommentRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(await JiraError(HttpStatusCode.NotFound));
        client
            .Setup(x => x.GetTransitionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(await JiraError(HttpStatusCode.ServiceUnavailable));
        var adapter = JiraAdapter(client, new CapturingLogger<JiraApiAdapter>());

        var search = await Capture(() => adapter.SearchTickets(Credentials(), "jql", 5, CancellationToken.None));
        var comment = await Capture(() => adapter.AddCommentToTicket(Credentials(), "SEC-1", new AdfDocument(), CancellationToken.None));
        var transition = await Capture(() => adapter.TransitionIssue(Credentials(), "SEC-1", "Done", CancellationToken.None));

        NotificationFailureReasons.For(search).Should().Be("Notification delivery failed: the destination returned HTTP 4xx.");
        NotificationFailureReasons.For(comment).Should().Be("Notification delivery failed: the destination returned HTTP 4xx.");
        NotificationFailureReasons.For(transition).Should().Be("Notification delivery failed: the destination returned HTTP 5xx.");
        ((int?)NotificationFailureReasons.StatusOf(search)).Should().Be(401);
    }

    // --- subscription runs -------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteQuery_OneRecipientFails_OthersAreStillNotifiedAndOnlyTheyKeepANotification()
    {
        var store = new JobStore();
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(x => x.SendNotification(It.Is<RecipientQueryResult>(y => y.RecipientId == 9), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(NotificationDeliveryException.ForStatus(HttpStatusCode.InternalServerError));
        var logger = new CapturingLogger<JobService>();

        var act = () => JobService(store, notifications.Object, logger, recipientIds: [9, 10]).ExecuteQuery(5, CancellationToken.None);

        await act.Should().ThrowAsync<NotificationDeliveryException>().WithMessage("Recipient 9: *HTTP 5xx.*");
        notifications.Verify(x => x.SendNotification(It.Is<RecipientQueryResult>(y => y.RecipientId == 10), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        var history = store.History.Should().ContainSingle().Subject;
        history.NotificationStatus.Should().Be(NotificationStatus.Failed);
        history.Comment.Should().Be("Recipient 9: Notification delivery failed: the destination returned HTTP 5xx.");
        history.Notifications.Select(x => x.RecipientId).Should().Equal(10);
        logger.Entries.Should().Contain(x => x.Level == LogLevel.Error && x.Message.Contains("1 of 2"));
    }

    [Test]
    public async Task ExecuteQuery_HostServiceWithRemoteText_StoresAndLogsOnlyAGenericReason()
    {
        var store = new JobStore();
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(x => x.SendNotification(It.IsAny<RecipientQueryResult>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException("Failed to send Webhook notification: InternalServerError. " + RemoteBody));
        var logger = new CapturingLogger<JobService>();

        var act = () => JobService(store, notifications.Object, logger, recipientIds: [9]).ExecuteQuery(5, CancellationToken.None);

        await act.Should().ThrowAsync<NotificationDeliveryException>();
        store.History.Single().Comment.Should().Be("Recipient 9: " + NotificationFailureReasons.Generic);
        logger.TextAtOrAbove(LogLevel.Information).Should().NotContain(RemoteToken);
    }

    [Test]
    public async Task ExecuteQuery_RecordsTheDataSourcesTheRunRead()
    {
        var store = new JobStore();

        await JobService(store, Mock.Of<INotificationService>(), new CapturingLogger<JobService>(), recipientIds: [9], dataSourceIds: [20, 10, 20])
            .ExecuteQuery(5, CancellationToken.None);

        store.History.Single().DataSourceIds.Should().Equal(10, 20);
    }

    [Test]
    public async Task ExecuteQuery_AllRecipientsNotified_KeepsEveryNotification()
    {
        var store = new JobStore();
        var notifications = new Mock<INotificationService>();

        await JobService(store, notifications.Object, new CapturingLogger<JobService>(), recipientIds: [9, 10]).ExecuteQuery(5, CancellationToken.None);

        var history = store.History.Single();
        history.NotificationStatus.Should().Be(NotificationStatus.NotificationSent);
        history.Comment.Should().BeNull();
        history.Notifications.Select(x => x.RecipientId).Should().Equal(9, 10);
    }

    [Test]
    public async Task ExecuteQuery_CancelledMidRun_KeepsOnlyTheNotificationsAlreadySent()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new JobStore();
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(x => x.SendNotification(It.Is<RecipientQueryResult>(y => y.RecipientId == 10), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback(cancellation.Cancel)
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var act = () => JobService(store, notifications.Object, new CapturingLogger<JobService>(), recipientIds: [9, 10, 11]).ExecuteQuery(5, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var history = store.History.Single();
        history.Notifications.Select(x => x.RecipientId).Should().Equal(9);
        history.NotificationStatus.Should().Be(NotificationStatus.Failed);
        history.Comment.Should().Contain("Recipient 10").And.Contain("Recipient 11");
    }

    [Test]
    public async Task ExecuteQuery_AnomalyWriteFailsAfterAFailedDelivery_TheFailureIsAlreadyRecorded()
    {
        var store = new JobStore();
        store.AnomalyConfigs.Add(new AnomalyConfig { SubscriptionId = 5, Enabled = true });
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(x => x.SendNotification(It.Is<RecipientQueryResult>(y => y.RecipientId == 9), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(NotificationDeliveryException.ForStatus(HttpStatusCode.InternalServerError));
        JobStore.SavedState? persistedBeforeAnomaly = null;
        var anomaly = new Mock<IAnomalyDetectionService>();
        anomaly
            .Setup(x => x.EvaluateAnomalyAsync(5, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnomalyEvaluationResult { IsAnomaly = true });
        anomaly
            .Setup(x => x.RecordAnomalyEventAsync(5, It.IsAny<AnomalyEvaluationResult>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback(() => persistedBeforeAnomaly = store.Saves.LastOrDefault())
            .ThrowsAsync(new InvalidOperationException("anomaly store unavailable"));

        var act = () => JobService(store, notifications.Object, new CapturingLogger<JobService>(), recipientIds: [9, 10], anomaly: anomaly.Object).ExecuteQuery(5, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("anomaly store unavailable");
        persistedBeforeAnomaly.Should().NotBeNull();
        persistedBeforeAnomaly!.Status.Should().Be(NotificationStatus.Failed);
        persistedBeforeAnomaly.Comment.Should().Be("Recipient 9: Notification delivery failed: the destination returned HTTP 5xx.");
        persistedBeforeAnomaly.NotifiedRecipientIds.Should().Equal(10);
    }

    [Test]
    public async Task ExecuteQuery_RequestCancelledAfterTheSends_StillRecordsTheFailures()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new JobStore();
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(x => x.SendNotification(It.Is<RecipientQueryResult>(y => y.RecipientId == 9), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(NotificationDeliveryException.ForStatus(HttpStatusCode.BadGateway));
        notifications
            .Setup(x => x.SendNotification(It.Is<RecipientQueryResult>(y => y.RecipientId == 10), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback(cancellation.Cancel)
            .Returns(Task.CompletedTask);

        var act = () => JobService(store, notifications.Object, new CapturingLogger<JobService>(), recipientIds: [9, 10]).ExecuteQuery(5, cancellation.Token);

        await act.Should().ThrowAsync<NotificationDeliveryException>();
        var saved = store.Saves.Last();
        saved.Status.Should().Be(NotificationStatus.Failed);
        saved.Comment.Should().StartWith("Recipient 9:");
        saved.NotifiedRecipientIds.Should().Equal(10);
    }

    [Test]
    public async Task DataContractAlert_FailedRecipient_LogsTheReasonOnlyAndTriesTheRest()
    {
        var store = new JobStore();
        store.Contracts.Add(new DataContract
        {
            Id = 3,
            Name = "orders",
            SchemaName = "sales",
            TableName = "orders",
            CronExpression = "0 * * * *",
            DataSource = new DataSource { Name = "warehouse", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "unused" },
            Recipients =
            [
                new Recipient { Id = 9, Name = "hook", Destination = "enc:a", NotificationType = NotificationType.Webhook },
                new Recipient { Id = 10, Name = "mail", Destination = "enc:b", NotificationType = NotificationType.Email },
            ],
        });
        var evaluation = new Mock<IDataQualityEvaluationService>();
        evaluation
            .Setup(x => x.EvaluateContractAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DataQualityEvaluationData { OverallScore = 10, TotalRules = 1, FailedRules = 1 });
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(x => x.SendNotification(It.Is<RecipientQueryResult>(y => y.RecipientId == 9), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException("relay said " + RemoteBody));
        var logger = new CapturingLogger<JobService>();

        await JobService(store, notifications.Object, logger, recipientIds: [], evaluation.Object).EvaluateDataContract(3, CancellationToken.None);

        notifications.Verify(x => x.SendNotification(It.Is<RecipientQueryResult>(y => y.RecipientId == 10), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        var error = logger.Entries.Should().ContainSingle(x => x.Level == LogLevel.Error).Subject;
        error.Message.Should().Contain("9").And.Contain(NotificationFailureReasons.Generic).And.Contain(nameof(BeaconException));
        error.Exception.Should().BeNull();
        logger.TextAtOrAbove(LogLevel.Trace).Should().NotContain(RemoteToken);
    }

    private static NotificationService Service(RecordingAdapter adapter, NotificationChannelOptions? options = null, ILogger<NotificationService>? logger = null)
    {
        return new NotificationService(
            new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict).Object,
            new AdapterFactory([adapter]),
            NotificationTestKit.Protector(),
            NotificationTestKit.Policy(options),
            Options.Create(options ?? new NotificationChannelOptions()),
            logger ?? NullLogger<NotificationService>.Instance);
    }

    private static RecipientQueryResult Delivery(NotificationType type, string destination, string? headersJson = null, int? notificationId = null, int? recipientId = null)
    {
        return new RecipientQueryResult
        {
            RecipientDestination = destination,
            RecipientNotificationType = type,
            HeadersJson = headersJson,
            NotificationId = notificationId,
            RecipientId = recipientId,
            QueryResult = Result(),
        };
    }

    private static QueryResult Result()
    {
        return new QueryResult
        {
            QueryResults = "[]",
            TotalRecords = 1,
            DataSourceName = "warehouse",
            SqlQuery = "SELECT 1",
            SubscriptionName = "daily",
            SubscriptionId = 5,
        };
    }

    private static ServiceProvider HttpServices(NotificationType type, ILogger<NotificationHttpSender> senderLogger)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(NotificationTestKit.PrivateNetworks(type, "127.0.0.1")));
        services.AddSingleton<IHostAddressResolver>(new FakeResolver());
        services.AddSingleton<OutboundAddressPolicy>();
        services.AddSingleton<NotificationDestinationPolicy>();
        services.AddSingleton(senderLogger);
        services.AddSingleton<NotificationHttpSender>();
        services.AddNotificationHttpClient();

        return services.BuildServiceProvider();
    }

    private static IAdapter Adapter(NotificationType type, IServiceProvider provider)
    {
        var sender = provider.GetRequiredService<NotificationHttpSender>();
        var configuration = new BeaconConfiguration();

        return type switch
        {
            NotificationType.Slack => new SlackAdapter(sender, configuration, NullLogger<SlackAdapter>.Instance),
            NotificationType.Teams => new TeamsAdapter(sender, configuration, NullLogger<TeamsAdapter>.Instance),
            _ => new WebhookAdapter(sender, provider.GetRequiredService<NotificationDestinationPolicy>(), configuration, NullLogger<WebhookAdapter>.Instance),
        };
    }

    private static async Task<ApiException> JiraError(HttpStatusCode statusCode)
    {
        return await ApiException.Create(
            new HttpRequestMessage(HttpMethod.Post, "https://acme.atlassian.net/rest/api/3/issue"),
            HttpMethod.Post,
            new HttpResponseMessage(statusCode) { Content = new StringContent(RemoteBody) },
            new RefitSettings());
    }

    private static JiraApiAdapter JiraAdapter(Mock<IJiraRestClient> client, ILogger<JiraApiAdapter> logger)
    {
        var factory = new Mock<IJiraRestClientFactory>();
        factory.Setup(x => x.CreateClient(It.IsAny<JiraCredentials>())).Returns(client.Object);

        return new JiraApiAdapter(factory.Object, logger);
    }

    private static JiraCredentials Credentials()
    {
        return new JiraCredentials("acme;SEC;svc@acme.eu;token");
    }

    private static async Task<Exception> Capture(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new AssertionException("Expected the call to fail.");
    }

    private static JobService JobService(
        JobStore store,
        INotificationService notifications,
        ILogger<JobService> logger,
        int[] recipientIds,
        IDataQualityEvaluationService? evaluation = null,
        IAnomalyDetectionService? anomaly = null,
        int[]? dataSourceIds = null)
    {
        var queryService = new Mock<IQueryService>();
        queryService
            .Setup(x => x.ExecuteQuery(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new QueryResult
            {
                QueryResults = "[]",
                TotalRecords = 3,
                DataSourceName = "warehouse",
                SqlQuery = "SELECT 1",
                SubscriptionName = "daily",
                SubscriptionId = 5,
                Recipients = [.. recipientIds.Select(x => new RecipientData { RecipientId = x, Name = $"r{x}", Destination = "enc:stored", NotificationType = NotificationType.Webhook })],
                DataSourceIds = dataSourceIds ?? [],
            });

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new JobTestContext(store));

        return new JobService(
            factory.Object,
            queryService.Object,
            notifications,
            Mock.Of<ITaskService>(),
            anomaly ?? Mock.Of<IAnomalyDetectionService>(),
            evaluation ?? Mock.Of<IDataQualityEvaluationService>(),
            logger);
    }

    private sealed class RecordingAdapter(NotificationType type) : IAdapter
    {
        public List<RecipientQueryResult> Received { get; } = [];

        public Exception? Failure { get; init; }

        public NotificationType NotificationType => type;

        public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount, CancellationToken cancellationToken = default)
        {
            if (Failure != null)
            {
                throw Failure;
            }

            Received.Add(recipientQueryResult);
            return Task.CompletedTask;
        }
    }

    private sealed class JobStore
    {
        public List<Subscription> Subscriptions { get; } =
        [
            new Subscription { Id = 5, QueryId = 1, CronExpression = "0 * * * *", NotificationTrigger = NotificationTrigger.Always },
        ];

        public List<QueryExecutionHistory> History { get; } = [];

        public List<DataContract> Contracts { get; } = [];

        public List<AnomalyConfig> AnomalyConfigs { get; } = [];

        /// <summary>What each save persisted for the run's history row, in order.</summary>
        public List<SavedState> Saves { get; } = [];

        public sealed record SavedState(NotificationStatus? Status, string? Comment, List<int> NotifiedRecipientIds);
    }

    /// <summary>A <see cref="BeaconContext"/> over in-memory sets for the jobs (§4.7: no in-memory provider).</summary>
    private sealed class JobTestContext(JobStore store) : BeaconContext(ContextOptions, "beacon")
    {
        private static readonly DbContextOptions<JobTestContext> ContextOptions =
            new DbContextOptionsBuilder<JobTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(Subscription))
            {
                return (DbSet<TEntity>)(object)MemorySet(store.Subscriptions);
            }

            if (typeof(TEntity) == typeof(QueryExecutionHistory))
            {
                var set = MemorySet(store.History);
                Mock.Get(set)
                    .Setup(x => x.AddAsync(It.IsAny<QueryExecutionHistory>(), It.IsAny<CancellationToken>()))
                    .Callback<QueryExecutionHistory, CancellationToken>((x, _) => store.History.Add(x));
                return (DbSet<TEntity>)(object)set;
            }

            if (typeof(TEntity) == typeof(AnomalyConfig))
            {
                return (DbSet<TEntity>)(object)MemorySet(store.AnomalyConfigs);
            }

            if (typeof(TEntity) == typeof(DataContract))
            {
                return (DbSet<TEntity>)(object)MemorySet(store.Contracts);
            }

            return base.Set<TEntity>();
        }

        // Like EF: a cancelled token fails the save before anything is written.
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = store.History.SingleOrDefault();
            store.Saves.Add(new JobStore.SavedState(
                history?.NotificationStatus,
                history?.Comment,
                history?.Notifications.Select(x => x.RecipientId).ToList() ?? []));
            return Task.FromResult(0);
        }

        private static DbSet<T> MemorySet<T>(List<T> rows) where T : class
        {
            var data = rows.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(rows.ToList().GetEnumerator()));
            set.As<IQueryable<T>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<T>(data.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => rows.ToList().GetEnumerator());
            return set.Object;
        }
    }

    /// <summary>Accepts loopback connections and answers each request, once fully read, with the same raw response.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _response;
        private readonly CancellationTokenSource _stop = new();

        private LoopbackServer(string response)
        {
            _response = response;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/hook/PATHSECRET";

        public static LoopbackServer Start(string response)
        {
            return new LoopbackServer(response);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = AnswerAsync(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }
        }

        // Headers ended and, when a Content-Length is given, that many body bytes arrived.
        private static bool IsComplete(string request)
        {
            var end = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }

            var lengthLine = request[..end]
                .Split("\r\n")
                .FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = lengthLine == null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim());

            return Encoding.UTF8.GetByteCount(request[(end + 4)..]) >= length;
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[16384];
                var received = new StringBuilder();
                while (!IsComplete(received.ToString()))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    received.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(_response), _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
        }
    }
}
