using Atlassian.Jira;
using Semantico.Api.Data.Entities;
using Semantico.Api.Helpers;
using Semantico.Api.Services;

namespace Semantico.Api.Adapters.Jira;

public interface IJiraAdapter
{
    Task SendJiraNotificationAsync(RecipientQueryResult recipientQueryResult);
}

public class JiraAdapter : IJiraAdapter
{
    public async Task SendJiraNotificationAsync(RecipientQueryResult recipientQueryResult)
    {
        var credentials = JiraHelper.GetJiraCredentials(recipientQueryResult.Recipient, recipientQueryResult.QueryResult.ProjectName);

        var jira = new JiraService(credentials.DomainName, credentials.Email, credentials.APIKey);

        var issues = await jira.GetIssuesAsync("");

        foreach (var issue in issues)
        {
            if (issue.Summary == recipientQueryResult.SubscriptionName)
            {
                if (recipientQueryResult.QueryResult.TotalRecords == 0)
                {
                    await jira.DeleteIssueAsync(issue);
                    return;
                }

                await CreateJiraCommentAsync(jira, recipientQueryResult, issue);
                return;
            }
        }

        if (recipientQueryResult.QueryResult.TotalRecords != 0)
        {
            var description = CompileQueryResultMessage(recipientQueryResult);

            await jira.CreateNewIssueAsync(credentials.Project, "", recipientQueryResult.SubscriptionName, description, "");
        }
    }

    private async Task CreateJiraCommentAsync(JiraService jira, RecipientQueryResult recipientQueryResult, Issue issue)
    {
        var commentBody = CompileQueryResultMessage(recipientQueryResult);
        await jira.AddCommentAsync(issue, commentBody);
    }

    private static string CompileQueryResultMessage(RecipientQueryResult recipientQueryResult)
    {
        return $"The Query produced: {recipientQueryResult.QueryResult.TotalRecords} results\nThe results are: \n{recipientQueryResult.QueryResult.QueryResults}";
    }
}