using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Subscriptions
{
    public class SubscriptionData
    {
        public int? SubscriptionId { get; init; }

        public required string Name { get; init; }

        public required int QueryId { get; init; }

        public required string CronExpression { get; init; }

        public required NotificationType NotificationType { get; init; }

        public required string Recipient { get; init; }

        public required List<SubscriptionParamaterData> Parameters { get; init; }
    }
}
