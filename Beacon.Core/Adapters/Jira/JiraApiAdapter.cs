using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Refit;
using Beacon.Core.Models;

namespace Beacon.Core.Adapters.Jira;

public interface IJiraApiAdapter
{
    Task<CreateWorkItemResult> CreateWorkItem(
        JiraCredentials credentials,
        string sessionId,
        string title,
        AdfDocument description,
        string issueType,
        CancellationToken cancellationToken,
        string? label = null,
        string? priorityName = null,
        string? parentKey = null);

    Task<List<JiraTicketSearchDto>> SearchTickets(JiraCredentials credentials, string jql, int maxResults, CancellationToken cancellationToken);

    Task AddCommentToTicket(JiraCredentials credentials, string ticketId, AdfDocument comment, CancellationToken cancellationToken);

    Task TransitionIssue(JiraCredentials credentials, string issueKey, string transitionName, CancellationToken cancellationToken);
}

public record CreateWorkItemResult(
    string TicketKey,
    string TicketUrl,
    bool IsNewTicket);

public record JiraTicketSearchDto(
    string Key,
    string Summary,
    string Description,
    string Status,
    string? Reporter,
    DateTime Created,
    DateTime Updated);

public class JiraApiAdapter(
    IJiraRestClientFactory clientFactory,
    ILogger<JiraApiAdapter> logger)
    : IJiraApiAdapter
{
    public async Task<CreateWorkItemResult> CreateWorkItem(
        JiraCredentials credentials,
        string sessionId,
        string title,
        AdfDocument description,
        string issueType,
        CancellationToken cancellationToken,
        string? label = null,
        string? priorityName = null,
        string? parentKey = null)
    {
        var client = clientFactory.CreateClient(credentials);

        // Use safe JQL query builder to prevent injection attacks
        var jqlQuery = JqlHelper.BuildSearchBySessionIdQuery(
            sessionId,
            credentials.Project,
            credentials.Email);
        var searchResponse = await client.SearchAsync(jqlQuery, 10, 0, "key,summary", cancellationToken);

        var existingIssue = searchResponse.Issues
            .Where(x => x.Fields.Summary == title)
            .FirstOrDefault();

        if (existingIssue == null)
        {
            try
            {
                var newIssue = await CreateNewIssueAsync(
                    client,
                    credentials.Project,
                    "",
                    title,
                    description,
                    issueType,
                    label,
                    priorityName ?? "Medium",
                    parentKey,
                    cancellationToken);
                var issueUrl = $"{credentials.DomainUrl}/browse/{newIssue.Key}";
                return new CreateWorkItemResult(newIssue.Key, issueUrl, true);
            }
            catch (ApiException ex)
            {
                var errorContent = ex.Content ?? "No error content";
                logger.LogError(ex, "Failed to create Jira issue. Project: {Project}, IssueType: {IssueType}, Error: {Error}",
                    credentials.Project, issueType, errorContent);
                throw new BeaconException($"Failed to create Jira issue: {errorContent}", ex);
            }
        }
        else
        {
            await CreateJiraCommentAsync(client, existingIssue.Key, description, cancellationToken);
            var issueUrl = $"{credentials.DomainUrl}/browse/{existingIssue.Key}";
            return new CreateWorkItemResult(existingIssue.Key, issueUrl, false);
        }
    }

    public async Task<List<JiraTicketSearchDto>> SearchTickets(JiraCredentials credentials, string jql, int maxResults, CancellationToken cancellationToken)
    {
        var client = clientFactory.CreateClient(credentials);
        var searchResponse = await client.SearchAsync(jql, maxResults, 0, "key,summary,description,status,reporter,created,updated", cancellationToken);
        return searchResponse.Issues.Select(MapToTicketSearchDto).ToList();
    }

    public async Task AddCommentToTicket(JiraCredentials credentials, string ticketId, AdfDocument comment, CancellationToken cancellationToken)
    {
        var client = clientFactory.CreateClient(credentials);
        await CreateJiraCommentAsync(client, ticketId, comment, cancellationToken);
    }

    public async Task TransitionIssue(JiraCredentials credentials, string issueKey, string transitionName, CancellationToken cancellationToken)
    {
        var client = clientFactory.CreateClient(credentials);
        var transitionsResponse = await client.GetTransitionsAsync(issueKey, cancellationToken);

        var transition = transitionsResponse.Transitions.FirstOrDefault(t =>
            t.Name.Equals(transitionName, StringComparison.OrdinalIgnoreCase));

        if (transition == null)
        {
            logger.LogWarning("Transition '{TransitionName}' not found for issue {IssueKey}. Available transitions: {Transitions}",
                transitionName, issueKey, string.Join(", ", transitionsResponse.Transitions.Select(t => t.Name)));
            return;
        }

        await client.TransitionIssueAsync(issueKey, new JiraTransitionRequest(new JiraTransitionId(transition.Id)), cancellationToken);
    }

    private static JiraTicketSearchDto MapToTicketSearchDto(JiraIssueResponse issue)
    {
        return new JiraTicketSearchDto(
            issue.Key,
            issue.Fields.Summary ?? string.Empty,
            ExtractTextFromJiraContent(issue.Fields.Description),
            issue.Fields.Status.Name,
            issue.Fields.Reporter?.EmailAddress,
            ParseDateTime(issue.Fields.Created),
            ParseDateTime(issue.Fields.Updated));
    }

    private static DateTime ParseDateTime(string? dateTimeString)
    {
        if (string.IsNullOrEmpty(dateTimeString))
        {
            return DateTime.UtcNow;
        }

        if (DateTime.TryParse(dateTimeString, out var result))
        {
            return result;
        }

        return DateTime.UtcNow;
    }
    
    private static async Task<JiraCommentResponse> CreateJiraCommentAsync(IJiraRestClient client, string issueKey, AdfDocument body, CancellationToken cancellationToken)
    {
        var commentRequest = new JiraAddCommentRequest(body);
        return await client.AddCommentAsync(issueKey, commentRequest, cancellationToken);
    }

    private static async Task<JiraCreateIssueResponse> CreateNewIssueAsync(
        IJiraRestClient client,
        string project,
        string assignee,
        string summary,
        AdfDocument description,
        string issueType,
        string? label,
        string? priorityName,
        string? parentKey,
        CancellationToken cancellationToken)
    {
        var createRequest = new JiraCreateIssueRequest(
            new JiraCreateIssueFields(
                new JiraProject(project),
                summary,
                description,
                new JiraIssueTypeRef(issueType),
                string.IsNullOrEmpty(assignee) ? null : new JiraAssignee(assignee),
                !string.IsNullOrEmpty(label) ? [label] : null,
                !string.IsNullOrEmpty(priorityName) ? new JiraPriorityRef(priorityName) : null,
                !string.IsNullOrEmpty(parentKey) ? new JiraParentRef(parentKey) : null));

        return await client.CreateIssueAsync(createRequest, cancellationToken);
    }

    private static string ExtractTextFromJiraContent(object? content)
    {
        if (content == null)
            return string.Empty;

        try
        {
            // Handle the case where content is already a string (from old SDK)
            if (content is string stringContent)
                return stringContent;

            // Handle Atlassian Document Format (ADF)
            var jsonString = content.ToString();
            if (string.IsNullOrEmpty(jsonString))
                return string.Empty;

            var jsonDoc = JsonDocument.Parse(jsonString);
            var textBuilder = new StringBuilder();

            ExtractTextFromJsonElement(jsonDoc.RootElement, textBuilder);
            return textBuilder.ToString().Trim();
        }
        catch
        {
            return content.ToString() ?? string.Empty;
        }
    }

    private static void ExtractTextFromJsonElement(JsonElement element, StringBuilder textBuilder)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var typeElement) && typeElement.GetString() == "text")
            {
                if (element.TryGetProperty("text", out var textElement))
                {
                    textBuilder.Append(textElement.GetString());
                }
            }
            else if (element.TryGetProperty("content", out var contentElement))
            {
                ExtractTextFromJsonElement(contentElement, textBuilder);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractTextFromJsonElement(item, textBuilder);
            }
        }
    }
}