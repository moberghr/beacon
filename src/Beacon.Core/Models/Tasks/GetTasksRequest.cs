using Beacon.Core.Helpers;

namespace Beacon.Core.Models.Tasks;

public record GetTasksRequest : ListRequest
{
    public int? SubscriptionId { get; init; }

    public bool? Resolved { get; init; }
}
