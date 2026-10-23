using System.Globalization;
using System.Reflection;
using System.Text;
using Beacon.Core.Adapters.Shared;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Adapters.Mail;

internal class EmailAdapter(IEmailAdapter emailAdapter) : IAdapter
{
    private const int MaxSubjectLength = 200;

    public NotificationType NotificationType => NotificationType.Email;

    public async Task SendNotificationAsync(
        RecipientQueryResult recipientQueryResult,
        int? lastNotificationResultCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var to = recipientQueryResult.RecipientDestination;
        var subject = BuildSubject($"{AdapterConstants.NotificationPrefix} {recipientQueryResult.QueryResult.DataSourceName} - {recipientQueryResult.QueryResult.SubscriptionName}");

        var htmlBody = Helpers.GenerateEmailContent(recipientQueryResult.QueryResult, recipientQueryResult.QueryResultFile);

        // The IEmailAdapter contract doesn't currently flow a CancellationToken into
        // the SMTP/Sendmail call — graceful shutdown is observed at this boundary.
        // A file too large even for its headers is not attached; the body says so.
        var attachment = recipientQueryResult.QueryResultFile is { Omitted: true } ? null : recipientQueryResult.QueryResultFile;
        await emailAdapter.SendEmailAsync(to, subject, htmlBody, attachment);
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
