using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Mail;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>The email adapter's recipient cap and the cancellation overload of <see cref="IEmailAdapter"/>.</summary>
[TestFixture]
public class EmailAdapterBoundsTests
{
    [Test]
    public async Task AMessageToFiftyAddresses_IsSent()
    {
        var mail = new Mock<IEmailAdapter>();
        mail
            .Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryResultFile?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await SendAsync(mail.Object, Destination(50));

        mail.Verify(x => x.SendEmailAsync(Destination(50), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryResultFile?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase(',')]
    [TestCase(';')]
    public async Task AMessageToMoreThanFiftyAddresses_IsRefused_AndNothingIsSent(char separator)
    {
        var mail = new Mock<IEmailAdapter>(MockBehavior.Strict);

        var act = () => SendAsync(mail.Object, Destination(51, separator));

        (await act.Should().ThrowAsync<NotificationDeliveryException>())
            .Which.Message.Should().Be(NotificationFailureReasons.TooManyRecipients);
    }

    [Test]
    public async Task TheAdapter_PassesItsTokenToTheSender()
    {
        using var source = new CancellationTokenSource();
        var mail = new Mock<IEmailAdapter>();
        mail
            .Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryResultFile?>(), source.Token))
            .Returns(Task.CompletedTask);

        await SendAsync(mail.Object, "ops@bank.example", source.Token);

        mail.Verify(x => x.SendEmailAsync("ops@bank.example", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryResultFile?>(), source.Token), Times.Once);
    }

    [Test]
    public async Task TheDefaultOverload_ForwardsToTheOneWithoutAToken()
    {
        var sender = new LegacySender();
        IEmailAdapter adapter = sender;

        await adapter.SendEmailAsync("ops@bank.example", "subject", "body", null, CancellationToken.None);

        sender.Calls.Should().Be(1);
    }

    [Test]
    public async Task TheDefaultOverload_StopsWaiting_WhenTheTokenIsCancelled()
    {
        var sender = new LegacySender { Completion = new TaskCompletionSource() };
        IEmailAdapter adapter = sender;
        using var source = new CancellationTokenSource();

        var send = adapter.SendEmailAsync("ops@bank.example", "subject", "body", null, source.Token);
        source.Cancel();

        await send.Invoking(x => x).Should().ThrowAsync<OperationCanceledException>();
    }

    private static Task SendAsync(IEmailAdapter mail, string destination, CancellationToken cancellationToken = default)
    {
        return new EmailAdapter(mail).SendNotificationAsync(
            new RecipientQueryResult
            {
                RecipientDestination = destination,
                RecipientNotificationType = NotificationType.Email,
                QueryResult = new QueryResult { QueryResults = "[]", TotalRecords = 1, DataSourceName = "ds", SqlQuery = "SELECT 1", SubscriptionName = "sub", SubscriptionId = 1 }
            },
            null,
            cancellationToken);
    }

    private static string Destination(int count, char separator = ',')
    {
        return string.Join(separator, Enumerable.Range(1, count).Select(x => $"user{x}@bank.example"));
    }

    private sealed class LegacySender : IEmailAdapter
    {
        public int Calls { get; private set; }

        public TaskCompletionSource? Completion { get; init; }

        public Task SendEmailAsync(string to, string subject, string body, QueryResultFile? queryResultAttachmentFile)
        {
            Calls++;
            return Completion?.Task ?? Task.CompletedTask;
        }
    }
}
