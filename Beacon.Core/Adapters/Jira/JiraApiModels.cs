using System.Text.Json.Serialization;
using Beacon.Core.Adapters.Jira;

namespace Adapters.Adapters.Jira;

public record JiraIssueResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("self")] string Self,
    [property: JsonPropertyName("fields")] JiraIssueFields Fields);

public record JiraIssueFields(
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("description")] object? Description,
    [property: JsonPropertyName("status")] JiraStatus Status,
    [property: JsonPropertyName("assignee")] JiraUser? Assignee,
    [property: JsonPropertyName("reporter")] JiraUser? Reporter,
    [property: JsonPropertyName("created")] string? Created,
    [property: JsonPropertyName("updated")] string? Updated,
    [property: JsonPropertyName("priority")] JiraPriority? Priority,
    [property: JsonPropertyName("issuetype")] JiraIssueType? IssueType,
    [property: JsonPropertyName("project")] JiraProjectInfo? Project,
    [property: JsonPropertyName("watches")] JiraWatcher? Watches,
    [property: JsonPropertyName("attachment")] JiraAttachment[]? Attachment,
    [property: JsonPropertyName("subtasks")] JiraSubTask[]? SubTasks,
    [property: JsonPropertyName("comment")] JiraCommentWrapper? Comment,
    [property: JsonPropertyName("issuelinks")] JiraIssueLink[]? IssueLinks,
    [property: JsonPropertyName("worklog")] JiraWorklogWrapper? Worklog,
    [property: JsonPropertyName("timetracking")] JiraTimeTracking? TimeTracking);

public record JiraStatus(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("iconUrl")] string? IconUrl);

public record JiraUser(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("emailAddress")] string? EmailAddress,
    [property: JsonPropertyName("accountType")] string? AccountType,
    [property: JsonPropertyName("active")] bool? Active,
    [property: JsonPropertyName("avatarUrls")] JiraAvatarUrls? AvatarUrls,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("self")] string? Self);

public record JiraPriority(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public record JiraIssueType(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public record JiraSearchResponse(
    [property: JsonPropertyName("startAt")] int StartAt,
    [property: JsonPropertyName("maxResults")] int MaxResults,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("issues")] JiraIssueResponse[] Issues);

public record JiraCreateIssueRequest(
    [property: JsonPropertyName("fields")] JiraCreateIssueFields Fields);

public record JiraCreateIssueFields(
    [property: JsonPropertyName("project")] JiraProject Project,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("description")] AdfDocument Description,
    [property: JsonPropertyName("issuetype")] JiraIssueTypeRef IssueType,
    [property: JsonPropertyName("assignee")] JiraAssignee? Assignee = null,
    [property: JsonPropertyName("labels")] string[]? Labels = null,
    [property: JsonPropertyName("priority")] JiraPriorityRef? Priority = null,
    [property: JsonPropertyName("parent")] JiraParentRef? Parent = null);

public record JiraProject(
    [property: JsonPropertyName("key")] string Key);

public record JiraPriorityRef(
    [property: JsonPropertyName("name")] string Name);

public record JiraComponentRef(
    [property: JsonPropertyName("id")] string Id);

public record JiraParentRef(
    [property: JsonPropertyName("key")] string Key);

public record JiraDescription(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("content")] JiraDescriptionContent[] Content);

public record JiraDescriptionContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] JiraDescriptionText[]? Content);

public record JiraDescriptionText(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

public record JiraIssueTypeRef(
    [property: JsonPropertyName("name")] string Name);

public record JiraAssignee(
    [property: JsonPropertyName("accountId")] string AccountId);

public record JiraCreateIssueResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("self")] string Self);

public record JiraCommentsResponse(
    [property: JsonPropertyName("startAt")] int StartAt,
    [property: JsonPropertyName("maxResults")] int MaxResults,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("comments")] JiraCommentResponse[] Comments);

public record JiraCommentResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("author")] JiraUser Author,
    [property: JsonPropertyName("body")] object Body,
    [property: JsonPropertyName("created")] string Created,
    [property: JsonPropertyName("updated")] string Updated);

public record JiraAddCommentRequest(
    [property: JsonPropertyName("body")] AdfDocument Body);

public record JiraAvatarUrls(
    [property: JsonPropertyName("16x16")] string? Size16,
    [property: JsonPropertyName("24x24")] string? Size24,
    [property: JsonPropertyName("32x32")] string? Size32,
    [property: JsonPropertyName("48x48")] string? Size48);

public record JiraProjectInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("avatarUrls")] JiraAvatarUrls? AvatarUrls,
    [property: JsonPropertyName("self")] string? Self,
    [property: JsonPropertyName("simplified")] bool? Simplified,
    [property: JsonPropertyName("style")] string? Style);

public record JiraWatcher(
    [property: JsonPropertyName("isWatching")] bool IsWatching,
    [property: JsonPropertyName("self")] string? Self,
    [property: JsonPropertyName("watchCount")] int WatchCount);

public record JiraAttachment(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("author")] JiraUser? Author,
    [property: JsonPropertyName("created")] string Created,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mimeType")] string? MimeType,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("thumbnail")] string? Thumbnail,
    [property: JsonPropertyName("self")] string? Self);

public record JiraSubTask(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] JiraIssueLinkType? Type,
    [property: JsonPropertyName("outwardIssue")] JiraLinkedIssue? OutwardIssue);

public record JiraIssueLink(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] JiraIssueLinkType? Type,
    [property: JsonPropertyName("outwardIssue")] JiraLinkedIssue? OutwardIssue,
    [property: JsonPropertyName("inwardIssue")] JiraLinkedIssue? InwardIssue);

public record JiraIssueLinkType(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("inward")] string? Inward,
    [property: JsonPropertyName("outward")] string? Outward);

public record JiraLinkedIssue(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("self")] string? Self,
    [property: JsonPropertyName("fields")] JiraLinkedIssueFields? Fields);

public record JiraLinkedIssueFields(
    [property: JsonPropertyName("status")] JiraStatus? Status);

public record JiraWorklog(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("author")] JiraUser? Author,
    [property: JsonPropertyName("updateAuthor")] JiraUser? UpdateAuthor,
    [property: JsonPropertyName("comment")] object? Comment,
    [property: JsonPropertyName("started")] string? Started,
    [property: JsonPropertyName("timeSpent")] string? TimeSpent,
    [property: JsonPropertyName("timeSpentSeconds")] int? TimeSpentSeconds,
    [property: JsonPropertyName("created")] string? Created,
    [property: JsonPropertyName("updated")] string? Updated,
    [property: JsonPropertyName("issueId")] string? IssueId,
    [property: JsonPropertyName("self")] string? Self);

public record JiraTimeTracking(
    [property: JsonPropertyName("originalEstimate")] string? OriginalEstimate,
    [property: JsonPropertyName("originalEstimateSeconds")] int? OriginalEstimateSeconds,
    [property: JsonPropertyName("remainingEstimate")] string? RemainingEstimate,
    [property: JsonPropertyName("remainingEstimateSeconds")] int? RemainingEstimateSeconds,
    [property: JsonPropertyName("timeSpent")] string? TimeSpent,
    [property: JsonPropertyName("timeSpentSeconds")] int? TimeSpentSeconds);

public record JiraCommentWrapper(
    [property: JsonPropertyName("comments")] JiraCommentResponse[]? Comments,
    [property: JsonPropertyName("startAt")] int StartAt,
    [property: JsonPropertyName("maxResults")] int MaxResults,
    [property: JsonPropertyName("total")] int Total);

public record JiraWorklogWrapper(
    [property: JsonPropertyName("worklogs")] JiraWorklog[]? Worklogs,
    [property: JsonPropertyName("startAt")] int StartAt,
    [property: JsonPropertyName("maxResults")] int MaxResults,
    [property: JsonPropertyName("total")] int Total);

public record JiraTransitionsResponse(
    [property: JsonPropertyName("transitions")] JiraTransition[] Transitions);

public record JiraTransition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("to")] JiraStatus? To);

public record JiraTransitionRequest(
    [property: JsonPropertyName("transition")] JiraTransitionId Transition);

public record JiraTransitionId(
    [property: JsonPropertyName("id")] string Id);