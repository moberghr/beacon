using Semantico.Core.Data.Enums;

namespace Semantico.Core.Adapters
{
    internal interface IAdapter
    {
        public NotificationType NotificationType { get; }
        /// <summary>
        /// Use this method when no previous notifications were sent.
        /// </summary>
        public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult);

        /// <summary>
        /// Use this method if previous notifications were sent.
        /// </summary>
        public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int lastNotificationResultCount);
    }
}