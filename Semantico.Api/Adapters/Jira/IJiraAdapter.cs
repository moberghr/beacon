using Atlassian.Jira;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Helpers;
using Semantico.Api.Services;

namespace Semantico.Api.Adapters.Jira;

public interface IJiraAdapter
{
    Task SendJiraNotificationAsync(int subscriptionId, RecipientQueryResult recipientQueryResult);
}

public class JiraAdapter : IJiraAdapter
{
    private readonly SemanticoContext _context;

    public JiraAdapter(SemanticoContext context)
    {
        _context = context;
    }

    public async Task SendJiraNotificationAsync(int subscriptionId, RecipientQueryResult recipientQueryResult)
    {
        var credentials = JiraHelper.GetJiraCredentials(recipientQueryResult.Recipient, recipientQueryResult.QueryResult.ProjectName);

        var jira = new JiraService(credentials.DomainName, credentials.Email, credentials.APIKey);

        var issues = await jira.GetIssuesAsync("");
        var existingIssue = issues
            .Where(x => x.Summary == recipientQueryResult.SubscriptionName)
            .SingleOrDefault();

        if (existingIssue != null)
        {
            var lastNotification = _context.Notifications
                .Where(x => x.SubscriptionId == subscriptionId)
                .OrderByDescending(x => x.CreatedTime)
                .First();

            if (recipientQueryResult.QueryResult.TotalRecords == 0)
            {
                await jira.DeleteIssueAsync(existingIssue);
                return;
            }

            if (lastNotification.ResultCount != recipientQueryResult.QueryResult.TotalRecords)
            {
                await CreateJiraCommentAsync(jira, recipientQueryResult, existingIssue);
            }

            return;
        }

        if (recipientQueryResult.QueryResult.TotalRecords != 0)
        {
            var issue = await jira.CreateNewIssueAsync(credentials.Project, "", recipientQueryResult.SubscriptionName, recipientQueryResult.QueryResult.SqlQuery, "");

            await CreateJiraCommentAsync(jira, recipientQueryResult, issue);

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