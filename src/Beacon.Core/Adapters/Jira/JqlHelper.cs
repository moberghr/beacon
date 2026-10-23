namespace Beacon.Core.Adapters.Jira;

/// <summary>
/// Helper utilities for building safe JQL (Jira Query Language) queries.
/// </summary>
internal static class JqlHelper
{
    /// <summary>The prefix of the labels that tie an issue to one subscription's deliveries to one recipient.</summary>
    public const string CorrelationLabelPrefix = "beacon-sub-";

    /// <summary>
    /// Escapes a string value for safe use in JQL queries.
    /// This prevents JQL injection by escaping special characters.
    /// </summary>
    /// <param name="value">The value to escape</param>
    /// <returns>Escaped string safe for JQL query interpolation</returns>
    /// <remarks>
    /// In JQL, strings are enclosed in double quotes and special characters must be escaped:
    /// - Double quotes (") must be escaped as \"
    /// - Backslashes (\) must be escaped as \\
    /// </remarks>
    public static string EscapeJqlString(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // Escape backslashes first (must be done before escaping quotes)
        var escaped = value.Replace("\\", "\\\\");

        // Then escape double quotes
        escaped = escaped.Replace("\"", "\\\"");

        return escaped;
    }

    /// <summary>
    /// Builds a JQL query for the open issues one subscription delivery created, found by its correlation label within
    /// the project.
    /// </summary>
    public static string BuildSearchByLabelQuery(string label, string project, string reporterEmail)
    {
        var escapedLabel = EscapeJqlString(label);
        var escapedProject = EscapeJqlString(project);
        var escapedEmail = EscapeJqlString(reporterEmail);

        return $"project = \"{escapedProject}\" AND labels = \"{escapedLabel}\" AND reporter = \"{escapedEmail}\" AND statusCategory != Done order by created DESC";
    }

    /// <summary>
    /// Builds a JQL query for the open issues of a project whose text names the subscription: the match for issues
    /// created before deliveries carried a correlation label.
    /// </summary>
    public static string BuildSearchBySubscriptionQuery(string subscriptionName, string project, string reporterEmail)
    {
        var escapedName = EscapeJqlString(subscriptionName);
        var escapedProject = EscapeJqlString(project);
        var escapedEmail = EscapeJqlString(reporterEmail);

        return $"text ~ \"{escapedName}\" AND project = \"{escapedProject}\" AND reporter = \"{escapedEmail}\" AND statusCategory != Done order by created DESC";
    }

    /// <summary>
    /// Builds a JQL query for searching existing issues by session ID and project.
    /// </summary>
    public static string BuildSearchBySessionIdQuery(string sessionId, string project, string reporterEmail)
    {
        var escapedSessionId = EscapeJqlString(sessionId);
        var escapedProject = EscapeJqlString(project);
        var escapedEmail = EscapeJqlString(reporterEmail);

        return $"text ~ \"{escapedSessionId}\" AND project = \"{escapedProject}\" AND reporter = \"{escapedEmail}\" AND statusCategory != Done order by created DESC";
    }
}
