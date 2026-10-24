using System.Globalization;
using System.Reflection;
using System.Text;
using Beacon.Core.Adapters.Shared;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;

namespace Beacon.Core.Adapters.Mail;

internal class EmailAdapter(IEmailAdapter emailAdapter) : IAdapter
{
    private const int MaxSubjectLength = 200;

    /// <summary>The most addresses one message may be sent to (a destination may list several, separated by commas or semicolons).</summary>
    public const int MaxRecipientsPerMessage = 50;

    public NotificationType NotificationType => NotificationType.Email;

    public async Task SendNotificationAsync(
        RecipientQueryResult recipientQueryResult,
        int? lastNotificationResultCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var to = recipientQueryResult.RecipientDestination;
        if (EmailRecipientCounter.Count(to) > MaxRecipientsPerMessage)
        {
            throw new NotificationDeliveryException(NotificationFailureReasons.TooManyRecipients);
        }

        var subject = BuildSubject($"{AdapterConstants.NotificationPrefix} {recipientQueryResult.QueryResult.DataSourceName} - {recipientQueryResult.QueryResult.SubscriptionName}");

        var htmlBody = Helpers.GenerateEmailContent(recipientQueryResult.QueryResult, recipientQueryResult.QueryResultFile);

        // A file too large even for its headers is not attached; the body says so.
        var attachment = recipientQueryResult.QueryResultFile is { Omitted: true } ? null : recipientQueryResult.QueryResultFile;
        await emailAdapter.SendEmailAsync(to, subject, htmlBody, attachment, cancellationToken);
    }

    private static string BuildSubject(string subject)
    {
        var line = new StringBuilder(subject.Length);
        foreach (var x in subject)
        {
            var separator = char.IsControl(x) || char.GetUnicodeCategory(x) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            if (separator)
            {
                if (line.Length > 0 && line[^1] != ' ')
                {
                    line.Append(' ');
                }

                continue;
            }

            line.Append(x);
        }

        var result = line.ToString().Trim();

        return result.Length > MaxSubjectLength ? result[..MaxSubjectLength] : result;
    }
}
