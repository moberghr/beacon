using Beacon.Core.Models;

namespace Beacon.Core.Services.Retention;

/// <summary>
/// Resolves the content-retention decision for one project: whether Beacon may persist the *content* of an MCP
/// interaction (questions, SQL, free-text errors, feedback notes) alongside its structure. Derived from the
/// effective MCP settings, so a deployment lock (<c>Beacon:Mcp:ForceNoContentRetention</c>) wins over every project.
/// </summary>
public interface IContentRetentionPolicy
{
    Task<ContentRetentionDecision> ResolveAsync(int? projectId, CancellationToken ct = default);
}

public sealed record ContentRetentionDecision(bool RetainQueryContent, bool AllowExplicitFeedbackContent)
{
    /// <summary>The content lock: no interaction content may be persisted for this project.</summary>
    public bool Locked => !RetainQueryContent;

    /// <summary>
    /// Explicit feedback content is a strict subset of query content — a project that retains nothing cannot
    /// keep a corrected SQL statement or a note either, whatever <c>AllowExplicitFeedbackContent</c> says.
    /// </summary>
    public static ContentRetentionDecision From(McpSettingsData settings) =>
        new(settings.RetainQueryContent, settings.AllowExplicitFeedbackContent && settings.RetainQueryContent);
}
