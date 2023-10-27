namespace Semantico.Api.Adapters.Jira;

public interface IJiraAdapter
{
    /// <summary>
    /// Use this method when no previous notifications were sent.
    /// If the query result count is larger than 0, create an issue and add the results as a comment.
    /// </summary>
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult);

    /// <summary>
    /// Use this method if previous notifications were sent.
    /// If last notification result count was 0, and the current query result count is larger than 0, create an issue and add the results as a comment.
    /// If the current query result count is 0, check if the issue exists on Jira and remove it.
    /// If the current query result count is not 0, if the issue was removed (manually) create it, add the new results as a comment
    /// </summary>
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int lastNotificationResultCount);
}
