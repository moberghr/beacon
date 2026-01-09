using Semantico.Core.Models;

namespace Semantico.Core.Adapters.Jira;

public class JiraCredentials
{
    /// <summary>
    /// Supported formats:
    /// - "your-domain-here;jira-project-key-here;your-email-here;your-cloud-api-key-here"
    /// - "https://api.atlassian.com/ex/jira/{cloudId};jira-project-key-here;your-email-here;your-cloud-api-key-here"
    /// </summary>
    public JiraCredentials(string recipient)
    {
        // Validate recipient is not null or whitespace
        if (string.IsNullOrWhiteSpace(recipient))
        {
            throw new SemanticoException(
                "Jira recipient cannot be empty. " +
                "Format: domain;project;email;apikey");
        }

        // Split and validate part count
        var parts = recipient.Split(';', StringSplitOptions.None);
        if (parts.Length != 4)
        {
            throw new SemanticoException(
                $"Jira recipient must have exactly 4 parts separated by semicolons. " +
                $"Found {parts.Length} parts. " +
                $"Format: domain;project;email;apikey");
        }

        // Validate and assign domain
        var domain = parts[0].Trim();
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new SemanticoException(
                "Jira domain (first part) cannot be empty. " +
                "Example: yourcompany or https://yourcompany.atlassian.net");
        }
        DomainUrl = domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? domain
            : $"https://{domain}.atlassian.net";

        // Validate and assign project key
        var project = parts[1].Trim();
        if (string.IsNullOrWhiteSpace(project))
        {
            throw new SemanticoException(
                "Jira project key (second part) cannot be empty. " +
                "Example: PROJ");
        }
        Project = project;

        // Validate and assign email
        var email = parts[2].Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new SemanticoException(
                "Jira email (third part) cannot be empty. " +
                "Example: user@example.com");
        }
        Email = email;

        // Validate and assign API key (most critical - security credential)
        var apiKey = parts[3].Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SemanticoException(
                "Jira API key (fourth part) cannot be empty. " +
                "API key is required for authentication.");
        }
        ApiKey = apiKey;
    }

    public readonly string DomainUrl;

    public readonly string Project;

    public readonly string Email;

    public readonly string ApiKey;
}