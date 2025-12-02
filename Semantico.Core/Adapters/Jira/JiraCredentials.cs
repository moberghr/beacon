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
        var data = recipient.Split(';');

        if (data.Length != 4)
        {
            throw new SemanticoException($"Jira recipient format is not correct.");
        }

        // Support both full URL and simple domain name formats
        DomainUrl = data[0].StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? data[0]
            : $"https://{data[0]}.atlassian.net";
        Project = data[1];
        Email = data[2];
        ApiKey = data[3];
    }

    public readonly string DomainUrl;

    public readonly string Project;

    public readonly string Email;

    public readonly string ApiKey;
}