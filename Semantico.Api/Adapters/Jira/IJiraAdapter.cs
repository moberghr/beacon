using Atlassian.Jira;
using Semantico.Api.Services;

namespace Semantico.Api.Adapters.Jira
{
    public interface IJiraAdapter
    {
        Task SendJiraNotificationAsync(RecipientQueryResult recipientQueryResult);
    }

}