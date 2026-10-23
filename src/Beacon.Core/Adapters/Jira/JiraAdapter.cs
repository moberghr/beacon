using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Adapters.Jira;

internal class JiraAdapter(
    IJiraApiAdapter jiraApiAdapter,
    ILogger<JiraAdapter> logger) : IAdapter
{
    public NotificationType NotificationType => NotificationType.Jira;

    /// <summary>The summary of the issue a subscription's deliveries go to; later deliveries find the issue by it.</summary>
    internal static string IssueSummary(string subscriptionName) => $"BEACON: {subscriptionName}";

    public async Task SendNotificationAsync(
        RecipientQueryResult recipientQueryResult,
        int? lastNotificationResultCount,
        CancellationToken cancellationToken = default)
    {
        var credentials = new JiraCredentials(recipientQueryResult.RecipientDestination);

        if (lastNotificationResultCount == null)
        {
            await CreateIssueAndCommentAsync(credentials, recipientQueryResult, cancellationToken);
            return;
        }

        var existingIssue = await FindExistingIssueAsync(credentials, recipientQueryResult, cancellationToken);

        if (recipientQueryResult.QueryResult.TotalRecords == 0)
        {
            if (existingIssue != null)
            {
                await jiraApiAdapter.TransitionIssue(credentials, existingIssue.Key, "Done", cancellationToken);
            }

            return;
        }

        if (existingIssue == null)
        {
            await CreateIssueAndCommentAsync(credentials, recipientQueryResult, cancellationToken);
            return;
        }

        await AddCommentToIssueAsync(credentials, existingIssue.Key, recipientQueryResult, cancellationToken);
    }

    /// <summary>
    /// The label that ties an issue to one subscription's deliveries to one recipient, so later deliveries find it
    /// without relying on free text; null when the delivery does not name both.
    /// </summary>
    internal static string? CorrelationLabel(RecipientQueryResult recipientQueryResult)
    {
        var subscriptionId = recipientQueryResult.QueryResult.SubscriptionId;
        var recipientId = recipientQueryResult.RecipientId;

        return subscriptionId == null || recipientId == null
            ? null
            : $"beacon-sub-{subscriptionId}-rcpt-{recipientId}";
    }

    // Issues are found by their correlation label inside the recipient's project. Issues created before the label existed
    // are still matched, for this release only, by the subscription's text and the exact summary within the same project.
    private async Task<JiraTicketSearchDto?> FindExistingIssueAsync(JiraCredentials credentials, RecipientQueryResult recipientQueryResult, CancellationToken cancellationToken)
    {
        var label = CorrelationLabel(recipientQueryResult);
        if (label != null)
        {
            var labelled = await jiraApiAdapter.SearchTickets(
                credentials,
                JqlHelper.BuildSearchByLabelQuery(label, credentials.Project, credentials.Email),
                1,
                cancellationToken);

            var match = labelled.FirstOrDefault();
            if (match != null)
            {
                return match;
            }
        }

        var legacy = await jiraApiAdapter.SearchTickets(
            credentials,
            JqlHelper.BuildSearchBySubscriptionQuery(recipientQueryResult.QueryResult.SubscriptionName, credentials.Project, credentials.Email),
            50,
            cancellationToken);

        return legacy
            .Where(x => x.Summary == IssueSummary(recipientQueryResult.QueryResult.SubscriptionName))
            .FirstOrDefault();
    }

    private async Task CreateIssueAndCommentAsync(JiraCredentials credentials, RecipientQueryResult recipientQueryResult, CancellationToken cancellationToken)
    {
        var description = Helpers.GenerateJiraAdf(recipientQueryResult.QueryResult);

        var result = await jiraApiAdapter.CreateWorkItem(
            credentials: credentials,
            sessionId: recipientQueryResult.QueryResult.SubscriptionName,
            title: IssueSummary(recipientQueryResult.QueryResult.SubscriptionName),
            description: description,
            issueType: "Task",
            label: CorrelationLabel(recipientQueryResult) is { } correlation ? $"Beacon {correlation}" : "Beacon",
            cancellationToken: cancellationToken);

        await AddCommentToIssueAsync(credentials, result.TicketKey, recipientQueryResult, cancellationToken);
    }

    private async Task AddCommentToIssueAsync(JiraCredentials credentials, string issueKey, RecipientQueryResult recipientQueryResult, CancellationToken cancellationToken)
    {
        var description = Helpers.GenerateJiraAdf(recipientQueryResult.QueryResult);

        if (recipientQueryResult.QueryResultFile != null)
        {
            // Limitation: attachment upload still uses the Jira v2 endpoint internally
            // and isn't wired through the new REST v3 adapter. The comment goes through;
            // file payload is dropped with a warning so the user can re-send manually.
            logger.LogWarning("Attachment upload for Jira issue {IssueKey} is not supported on the REST v3 adapter. File: {FileName}",
                issueKey, recipientQueryResult.QueryResultFile.Name);
        }

        await jiraApiAdapter.AddCommentToTicket(credentials, issueKey, description, cancellationToken);
    }
}