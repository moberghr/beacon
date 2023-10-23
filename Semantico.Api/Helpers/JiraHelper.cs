using Semantico.Api.Adapters;
using Semantico.Api.Types;

namespace Semantico.Api.Helpers;

public class JiraHelper
{
    public static JiraCredentials GetJiraCredentials(string recipient, string projectName)
    {
        var data = recipient.Split(':');

        if (data.Length != 3)
        {
            throw new SemanticoException($"Jira recipient format is not correct.");
        }

        return new JiraCredentials
        {
            Project = projectName,
            DomainName = $"https://{data[0]}.atlassian.net",
            Email = data[1],
            APIKey = data[2],
        };
    }
}
