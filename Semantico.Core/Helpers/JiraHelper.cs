using Semantico.Core.Adapters.Jira;
using Semantico.Core.Models;

namespace Semantico.Core.Helpers;

internal class JiraHelper
{
    // "your-domain-here;jiraProjectKey;your-email-here;your-cloud-api-here
    public static JiraCredentials GetJiraCredentials(string recipient)
    {
        var data = recipient.Split(';');

        if (data.Length != 4)
        {
            throw new SemanticoException($"Jira recipient format is not correct.");
        }

        return new JiraCredentials
        {
            DomainUrl = $"https://{data[0]}.atlassian.net",
            Project = data[1],
            Email = data[2],
            ApiKey = data[3]
        };
    }
}
