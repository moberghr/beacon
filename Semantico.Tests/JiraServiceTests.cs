using Atlassian.Jira;
using Atlassian.Jira.Remote;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Semantico.Api.Services;
using System.Collections.Generic;
using Xunit;

namespace Semantico.Tests;
public class JiraServiceTests
{
    JiraService service = new JiraService(domainUrl, email, apiKey);

    private static string projectKey = "NP";
    private static string domainUrl = "https://your-domain-here.atlassian.net";
    private static string email = "your-email-here";
    private static string apiKey = "your-cloud-api-here";

    [Fact]
    public async Task AddCommentToIssue()
    {
        var createdIssue = await service.CreateNewIssueAsync(projectKey, "", "testSummary", "testDescription", "testComment");

        Assert.NotNull(createdIssue);

        var createdComment = await service.AddCommentAsync(createdIssue, "TestCommentJiraUnitTest");

        Assert.NotNull(createdComment);

        var fetchedComments = await createdIssue.GetCommentsAsync();

        var filteredComment = fetchedComments.Where(x => x.Body == createdComment.Body).SingleOrDefault();

        Assert.NotNull(filteredComment);

        await service.DeleteIssueAsync(createdIssue);
    }

    [Fact]
    public async Task CreateAndRemoveJiraIssue()
    {
        var createdIssue = await service.CreateNewIssueAsync(projectKey, "", "testSummary", "testDescription", "testComment");

        Assert.NotNull(createdIssue);

        var fetchedIssue = await service.GetIssueAsync(createdIssue.JiraIdentifier);

        Assert.NotNull(fetchedIssue);

        Assert.Equal(createdIssue.JiraIdentifier, fetchedIssue.JiraIdentifier);

        await service.DeleteIssueAsync(fetchedIssue);

        await Assert.ThrowsAsync<ResourceNotFoundException>(async () =>
        {
            await service.GetIssueAsync(createdIssue.JiraIdentifier);
        });
    }

}
