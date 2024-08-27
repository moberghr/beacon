using Atlassian.Jira;
using Semantico.Core.Helpers;

namespace Semantico.Core.Adapters.Jira;

internal class JiraAdapter : IJiraAdapter
{
    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult)
    {
        var credentials = JiraHelper.GetJiraCredentials(recipientQueryResult.Recipient);

        var jiraClient = Atlassian.Jira.Jira.CreateRestClient(credentials.DomainUrl, credentials.Email, credentials.ApiKey);

        await CreateIssueAndCommentAsync(credentials, jiraClient, recipientQueryResult);
    }

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int lastNotificationResultCount)
    {
        var credentials = JiraHelper.GetJiraCredentials(recipientQueryResult.Recipient);

        var jiraClient = Atlassian.Jira.Jira.CreateRestClient(credentials.DomainUrl, credentials.Email, credentials.ApiKey);

        var jqlQuery = $"text ~ \"{recipientQueryResult.SubscriptionName}\" AND reporter = \"{credentials.Email}\" order by created DESC";
        var issues =  (await jiraClient.Issues.GetIssuesFromJqlAsync(jqlQuery)).ToList();

        var existingIssue = issues
            .Where(x => x.Summary == recipientQueryResult.SubscriptionName)
            .FirstOrDefault();

        if (recipientQueryResult.QueryResult.TotalRecords == 0)
        {
            if (existingIssue != null)
            {
                await jiraClient.Issues.DeleteIssueAsync(existingIssue.Key.Value);
            }

            return;
        }

        if (existingIssue == null)
        {
            existingIssue = await CreateIssueAndCommentAsync(credentials, jiraClient, recipientQueryResult);
            return;
        }

        if (lastNotificationResultCount != recipientQueryResult.QueryResult.TotalRecords)
        {
            await CreateJiraCommentAsync(credentials.Email, existingIssue, recipientQueryResult);
        }
    }

    private async Task<Issue> CreateIssueAndCommentAsync(JiraCredentials credentials, Atlassian.Jira.Jira jiraClient, RecipientQueryResult recipientQueryResult)
    {
        var issue = await CreateNewIssueAsync(jiraClient, credentials.Project, "", recipientQueryResult.SubscriptionName, recipientQueryResult.QueryResult.SqlQuery);

        await CreateJiraCommentAsync(credentials.Email, issue, recipientQueryResult);

        return issue;
    }

    private static string CompileQueryResultMessage(RecipientQueryResult recipientQueryResult)
    {
        return $"The Query produced: {recipientQueryResult.QueryResult.TotalRecords} results\n" +
               $"The results are: \n" +
               $"{recipientQueryResult.QueryResult.QueryResults}";
    }

    private async Task<Comment> CreateJiraCommentAsync(string currentUser, Issue issue, RecipientQueryResult recipientQueryResult)
    {
        var commentBody = CompileQueryResultMessage(recipientQueryResult);

        var comment = new Comment
        {
            Author = currentUser,
            Body = commentBody
        };

        return await issue.AddCommentAsync(comment);
    }

    private async Task<Issue> CreateNewIssueAsync(Atlassian.Jira.Jira jiraClient, string project, string assignee, string summary, string description)
    {
        var issue = jiraClient.CreateIssue(project);
        issue.Type = "Task";
        issue.Assignee = assignee;
        issue.Summary = summary;
        issue.Description = description;

        await issue.SaveChangesAsync();
        return issue;
    }
}