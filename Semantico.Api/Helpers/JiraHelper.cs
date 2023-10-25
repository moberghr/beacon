using Semantico.Api.Adapters.Jira;
using Semantico.Api.Types;

namespace Semantico.Api.Helpers;

public class JiraHelper
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
            DomainName = $"https://{data[0]}.atlassian.net",
            Project = data[1],
            Email = data[2],
            APIKey = data[3]
        };
    }
}
