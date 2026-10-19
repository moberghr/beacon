using System.Text.Json.Serialization;

namespace Beacon.Core.Adapters.Jira;

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
    [property: JsonPropertyName("issuetype")] JiraIssueType? IssueType);

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

public record JiraParentRef(
    [property: JsonPropertyName("key")] string Key);

public record JiraIssueTypeRef(
    [property: JsonPropertyName("name")] string Name);

public record JiraAssignee(
    [property: JsonPropertyName("accountId")] string AccountId);

public record JiraCreateIssueResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("self")] string Self);

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