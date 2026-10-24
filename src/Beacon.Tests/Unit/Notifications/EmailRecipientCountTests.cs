using Beacon.Core.Adapters.Mail;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Notifications;

[TestFixture]
public class EmailRecipientCountTests
{
    [Test]
    public void ADisplayNameWithAComma_CountsOnce()
    {
        EmailRecipientCounter.Count("\"Doe, John\" <john@bank.example>").Should().Be(1);
    }

    [Test]
    public void RepeatedAddresses_CountOnce()
    {
        EmailRecipientCounter.Count("a@bank.example, A@bank.example; b@bank.example").Should().Be(2);
    }

    [Test]
    public void ADestinationThatDoesNotParse_CountsEverySeparatedPart()
    {
        EmailRecipientCounter.Count("not an address, another").Should().Be(2);
    }

    [Test]
    public void SavingAnEmailDestinationOverTheCap_WarnsAndStillSaves()
    {
        var logger = new CapturingLogger();
        var editor = new RecipientSecretEditor(NotificationTestKit.Policy(), NotificationTestKit.Protector(), logger);
        var destination = string.Join(',', Enumerable.Range(1, EmailAdapter.MaxRecipientsPerMessage + 1).Select(x => $"user{x}@bank.example"));

        var (stored, _) = editor.Prepare(NotificationType.Email, destination, null, null);

        stored.Should().NotBeNullOrEmpty();
        logger.Levels.Should().Equal(LogLevel.Warning);
    }

    private sealed class CapturingLogger : ILogger<RecipientSecretEditor>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
        }
    }
}
