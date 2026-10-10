using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Refit;
using Beacon.Core.Models;

namespace Beacon.Core.Notifications;

/// <summary>
/// A notification could not be delivered. The message is always one of <see cref="NotificationFailureReasons"/>: it is
/// stored on the execution history and shown to users, so it never carries the remote response body, the destination
/// or the raw connection error. Remote detail is logged at Debug level only.
/// </summary>
public sealed class NotificationDeliveryException : BeaconException
{
    public NotificationDeliveryException(string reason, HttpStatusCode? statusCode = null)
        : base(reason)
    {
        StatusCode = statusCode;
    }

    /// <summary>The destination's HTTP status, when it answered with one; for logs, not shown to users.</summary>
    public HttpStatusCode? StatusCode { get; }

    public static NotificationDeliveryException ForStatus(HttpStatusCode statusCode)
    {
        return new NotificationDeliveryException(NotificationFailureReasons.ForStatus(statusCode), statusCode);
    }
}

/// <summary>The closed set of failure reasons a notification delivery can record.</summary>
public static class NotificationFailureReasons
{
    public const string Generic = "Notification delivery failed.";

    public const string ConnectionBlocked = "Notification delivery failed: connection refused by policy.";

    public const string ConnectionFailed = "Notification delivery failed: connection failed.";

    public const string TimedOut = "Notification delivery failed: the destination did not respond in time.";

    public const string DestinationNotAllowed = "Notification delivery failed: the destination is not allowed by policy.";

    public const string DestinationUnreadable = "Notification delivery failed: the stored destination could not be read.";

    public const string DestinationNotEncrypted = "Notification delivery failed: the stored destination is not encrypted.";

    private static readonly Regex RecordedComment = BuildRecordedCommentPattern();

    /// <summary>The reason for an HTTP response that was not a success: only the status class is kept.</summary>
    public static string ForStatus(HttpStatusCode statusCode)
    {
        var statusClass = (int)statusCode / 100;

        return statusClass is >= 1 and <= 5
            ? $"Notification delivery failed: the destination returned HTTP {statusClass}xx."
            : Generic;
    }

    /// <summary>
    /// Maps any delivery exception onto a reason. A <see cref="NotificationDeliveryException"/> keeps its own; anything
    /// else is reduced to its category, so no remote text survives.
    /// </summary>
    public static string For(Exception exception)
    {
        return exception switch
        {
            NotificationDeliveryException x => x.Message,
            ApiException x => ForStatus(x.StatusCode),
            HttpRequestException x when HasBlockedConnection(x) => ConnectionBlocked,
            HttpRequestException { StatusCode: { } statusCode } => ForStatus(statusCode),
            HttpRequestException => ConnectionFailed,
            TaskCanceledException { InnerException: TimeoutException } => TimedOut,
            OutboundConnectionBlockedException => ConnectionBlocked,
            _ => Generic,
        };
    }

    /// <summary>
    /// What a reader of the run history is shown for a run's stored comment: the comment when it is a reason recorded
    /// from this set, alone or in the per-recipient form a failed delivery records (<c>Recipient 7: reason</c>, one per
    /// recipient, separated by spaces); anything else, such as failure detail recorded by earlier versions, as
    /// <see cref="Generic"/>. Null stays null.
    /// </summary>
    public static string? Displayable(string? comment)
    {
        if (comment == null)
        {
            return null;
        }

        return RecordedComment.IsMatch(comment) ? comment : Generic;
    }

    /// <summary>The HTTP status a delivery failure carries, if the destination answered with one.</summary>
    public static HttpStatusCode? StatusOf(Exception exception)
    {
        return exception switch
        {
            NotificationDeliveryException x => x.StatusCode,
            ApiException x => x.StatusCode,
            HttpRequestException x => x.StatusCode,
            _ => null,
        };
    }

    private static Regex BuildRecordedCommentPattern()
    {
        var reasons = new[]
            {
                Generic,
                ConnectionBlocked,
                ConnectionFailed,
                TimedOut,
                DestinationNotAllowed,
                DestinationUnreadable,
                DestinationNotEncrypted
            }
            .Concat(Enumerable.Range(1, 5).Select(x => ForStatus((HttpStatusCode)(x * 100))))
            .Select(Regex.Escape);
        var reason = $"(?:{string.Join("|", reasons)})";
        var perRecipient = $"Recipient [0-9]*: {reason}";

        return new Regex(
            $"^(?:{reason}|{perRecipient}(?: {perRecipient})*)$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
    }

    private static bool HasBlockedConnection(Exception exception)
    {
        for (var current = exception.InnerException; current != null; current = current.InnerException)
        {
            if (current is OutboundConnectionBlockedException)
            {
                return true;
            }
        }

        return false;
    }
}
