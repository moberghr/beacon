using System.Net.Http.Headers;
using System.Text;
using Adapters.Adapters.Jira;
using Refit;

namespace Semantico.Core.Adapters.Jira;

public interface IJiraRestClient
{
    [Get("/rest/api/3/issue/{issueIdOrKey}")]
    Task<JiraIssueResponse> GetIssueAsync(string issueIdOrKey, CancellationToken cancellationToken = default);

    [Get("/rest/api/3/search/jql")]
    Task<JiraSearchResponse> SearchAsync(string jql, int maxResults = 50, int startAt = 0, string? fields = null, CancellationToken cancellationToken = default);

    [Post("/rest/api/3/issue")]
    Task<JiraCreateIssueResponse> CreateIssueAsync([Body] JiraCreateIssueRequest request, CancellationToken cancellationToken = default);

    [Get("/rest/api/3/issue/{issueIdOrKey}/comment")]
    Task<JiraCommentsResponse> GetCommentsAsync(string issueIdOrKey, CancellationToken cancellationToken = default);

    [Post("/rest/api/3/issue/{issueIdOrKey}/comment")]
    Task<JiraCommentResponse> AddCommentAsync(string issueIdOrKey, [Body] JiraAddCommentRequest request, CancellationToken cancellationToken = default);

    [Get("/rest/api/3/issue/{issueIdOrKey}/transitions")]
    Task<JiraTransitionsResponse> GetTransitionsAsync(string issueIdOrKey, CancellationToken cancellationToken = default);

    [Post("/rest/api/3/issue/{issueIdOrKey}/transitions")]
    Task TransitionIssueAsync(string issueIdOrKey, [Body] JiraTransitionRequest request, CancellationToken cancellationToken = default);
}

public interface IJiraRestClientFactory
{
    IJiraRestClient CreateClient(JiraCredentials credentials);
}

public class JiraRestClientFactory(IHttpClientFactory httpClientFactory) : IJiraRestClientFactory
{
    public IJiraRestClient CreateClient(JiraCredentials credentials)
    {
        var httpClient = httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(credentials.DomainUrl);

        var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{credentials.Email}:{credentials.ApiKey}"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);

        return RestService.For<IJiraRestClient>(httpClient);
    }
}