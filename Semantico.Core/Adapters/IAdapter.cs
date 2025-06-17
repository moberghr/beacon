using Semantico.Core.Data.Enums;

namespace Semantico.Core.Adapters;

internal interface IAdapter
{
    public NotificationType NotificationType { get; }
    
    /// <summary>
    /// If lastNotificationResultCount is not null, it indicates that this is a follow-up notification. It will not create a new issue in Jira, but will instead update the existing one.
    /// </summary>
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount);
}