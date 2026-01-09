namespace Semantico.Core.Adapters.Jira;

/// <summary>
/// Helper utilities for building safe JQL (Jira Query Language) queries.
/// </summary>
internal static class JqlHelper
{
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
    /// Builds a JQL query for searching Semantico notifications by subscription name.
    /// </summary>
    public static string BuildSearchBySubscriptionQuery(string subscriptionName, string reporterEmail)
    {
        var escapedName = EscapeJqlString(subscriptionName);
        var escapedEmail = EscapeJqlString(reporterEmail);

        return $"text ~ \"{escapedName}\" AND reporter = \"{escapedEmail}\" order by created DESC";
    }

    /// <summary>
    /// Builds a JQL query for searching existing issues by session ID and project.
    /// </summary>
    public static string BuildSearchBySessionIdQuery(string sessionId, string project, string reporterEmail)
    {
        var escapedSessionId = EscapeJqlString(sessionId);
        var escapedProject = EscapeJqlString(project);
        var escapedEmail = EscapeJqlString(reporterEmail);

        return $"text ~ \"{escapedSessionId}\" AND project = \"{escapedProject}\" AND reporter = \"{escapedEmail}\" order by created DESC";
    }
}
