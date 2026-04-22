using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Adapters.Jira;

internal class JiraAdapter(
    IJiraApiAdapter jiraApiAdapter,
    ILogger<JiraAdapter> logger) : IAdapter
{
    public NotificationType NotificationType => NotificationType.Jira;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var credentials = new JiraCredentials(recipientQueryResult.RecipientDestination);
        var cancellationToken = CancellationToken.None;

        if (lastNotificationResultCount == null)
        {
            await CreateIssueAndCommentAsync(credentials, recipientQueryResult, cancellationToken);
            return;
        }

        var jqlQuery = JqlHelper.BuildSearchBySubscriptionQuery(
            recipientQueryResult.QueryResult.SubscriptionName,
            credentials.Email);
        var issues = await jiraApiAdapter.SearchTickets(credentials, jqlQuery, 50, cancellationToken);

        var existingIssue = issues
            .Where(x => x.Summary == recipientQueryResult.QueryResult.SubscriptionName)
            .FirstOrDefault();

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

    private async Task CreateIssueAndCommentAsync(JiraCredentials credentials, RecipientQueryResult recipientQueryResult, CancellationToken cancellationToken)
    {
        var description = Helpers.GenerateJiraAdf(recipientQueryResult.QueryResult);

        var result = await jiraApiAdapter.CreateWorkItem(
            credentials: credentials,
            sessionId: recipientQueryResult.QueryResult.SubscriptionName,
            title: $"BEACON: {recipientQueryResult.QueryResult.SubscriptionName}",
            description: description,
            issueType: "Task",
            label:"Beacon",
            cancellationToken: cancellationToken);

        await AddCommentToIssueAsync(credentials, result.TicketKey, recipientQueryResult, cancellationToken);
    }

    private async Task AddCommentToIssueAsync(JiraCredentials credentials, string issueKey, RecipientQueryResult recipientQueryResult, CancellationToken cancellationToken)
    {
        var description = Helpers.GenerateJiraAdf(recipientQueryResult.QueryResult);

        if (recipientQueryResult.QueryResultFile != null)
        {
            // TODO: Attachment upload is not yet supported in the new REST API
            logger.LogWarning("Attachment upload for Jira issue {IssueKey} is not supported. File: {FileName}",
                issueKey, recipientQueryResult.QueryResultFile.Name);
        }

        await jiraApiAdapter.AddCommentToTicket(credentials, issueKey, description, cancellationToken);
    }
}