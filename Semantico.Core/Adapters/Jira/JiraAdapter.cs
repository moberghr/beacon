using Atlassian.Jira;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Adapters.Jira;

internal class JiraAdapter : IAdapter
{
    public NotificationType NotificationType => NotificationType.Jira;
    
    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var credentials = new JiraCredentials(recipientQueryResult.RecipientDestination);

        var jiraClient = Atlassian.Jira.Jira.CreateRestClient(credentials.DomainUrl, credentials.Email, credentials.ApiKey);

        if (lastNotificationResultCount == null)
        {
            await CreateIssueAndCommentAsync(credentials, jiraClient, recipientQueryResult);
            return;
        }

        var jqlQuery = $"text ~ \"{recipientQueryResult.QueryResult.SubscriptionName}\" AND reporter = \"{credentials.Email}\" order by created DESC";
        var issues = (await jiraClient.Issues.GetIssuesFromJqlAsync(jqlQuery)).ToList();

        var existingIssue = issues
            .Where(x => x.Summary == recipientQueryResult.QueryResult.SubscriptionName)
            .FirstOrDefault();

        if (recipientQueryResult.QueryResult.TotalRecords == 0)
        {
            if (existingIssue != null)
            {
                await existingIssue.WorkflowTransitionAsync("Done");
            }

            return;
        }

        if (existingIssue == null)
        {
            await CreateIssueAndCommentAsync(credentials, jiraClient, recipientQueryResult);
            return;
        }

        //if (lastNotificationResultCount != recipientQueryResult.QueryResult.TotalRecords)
        {
            await CreateJiraCommentAsync(credentials.Email, existingIssue, recipientQueryResult);
        }
    }

    private async Task<Issue> CreateIssueAndCommentAsync(JiraCredentials credentials, Atlassian.Jira.Jira jiraClient, RecipientQueryResult recipientQueryResult)
    {
        var description = Helpers.GenerateJiraContent(recipientQueryResult.QueryResult);
        
        var issue = await CreateNewIssueAsync(jiraClient, credentials.Project, "", recipientQueryResult.QueryResult.SubscriptionName, description);

        await CreateJiraCommentAsync(credentials.Email, issue, recipientQueryResult);

        return issue;
    }

    private async Task<Comment> CreateJiraCommentAsync(string currentUser, Issue issue, RecipientQueryResult recipientQueryResult)
    {
        var description = Helpers.GenerateJiraContent(recipientQueryResult.QueryResult);

        var comment = new Comment
        {
            Author = currentUser,
            Body = description
        };

        if (recipientQueryResult.QueryResultFile != null)
        {
            var attachment = new UploadAttachmentInfo(recipientQueryResult.QueryResultFile.Name, recipientQueryResult.QueryResultFile.Data);

            await issue.AddAttachmentAsync([attachment]);
        }

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