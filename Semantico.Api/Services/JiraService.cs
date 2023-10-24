using Atlassian.Jira;

namespace Semantico.Api.Services;

public interface IJiraService
{
    Task<Issue> GetIssueAsync(string id);

    Task<List<Issue>> GetIssuesAsync(string JQL);

    Task<Comment> AddCommentAsync(Issue issue, string comment);

    Task<Issue> CreateNewIssueAsync(string project, string assignee, string summary, string description, string comment);

    Task DeleteIssueAsync(Issue issue);
}

public class JiraService : IJiraService
{
    private Jira _jiraClient;
    private string _user;

    public JiraService(string server, string user, string password)
    {
        _user = user;
        _jiraClient = Jira.CreateRestClient(server, user, password);
    }

    public async Task<Issue> GetIssueAsync(string id)
    {
        return await _jiraClient.Issues.GetIssueAsync(id);
    }

    public async Task<List<Issue>> GetIssuesAsync(string JQL)
    {
        return (await _jiraClient.Issues.GetIssuesFromJqlAsync(JQL)).ToList();
    }

    public async Task<Comment> AddCommentAsync(Issue issue, string commentBody)
    {
        var comment = new Comment
        {
            Author = _user,
            Body = commentBody
        };
        return await issue.AddCommentAsync(comment);
    }

    public async Task<Issue> CreateNewIssueAsync(string project, string assignee, string summary, string description, string comment)
    {
        var issue = _jiraClient.CreateIssue(project);
        issue.Type = "Task";
        issue.Assignee = assignee;
        issue.Summary = summary;
        issue.Description = description;

        await issue.SaveChangesAsync();
        return issue;
    }

    public async Task DeleteIssueAsync(Issue issue)
    {
        await _jiraClient.Issues.DeleteIssueAsync(issue.Key.Value);
    }
}
