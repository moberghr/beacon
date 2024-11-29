using Semantico.Core.Models;

namespace Semantico.Core.Adapters.Jira;

internal class JiraCredentials
{
    /// <summary>
    /// "your-domain-here;jira-project-key-here;your-email-here;your-cloud-api-key-here"
    /// </summary>
    public JiraCredentials(string recipient)
    {
        var data = recipient.Split(';');

        if (data.Length != 4)
        {
            throw new SemanticoException($"Jira recipient format is not correct.");
        }

        DomainUrl = $"https://{data[0]}.atlassian.net";
        Project = data[1];
        Email = data[2];
        ApiKey = data[3];
    }

    public readonly string DomainUrl;

    public readonly string Project;

    public readonly string Email;

    public readonly string ApiKey;
}