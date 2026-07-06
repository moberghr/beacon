using Beacon.Core.Helpers;

namespace Beacon.Core.Models.Tasks;

public class GetTasksRequest : SortedListRequest
{
    public int? SubscriptionId { get; set; }
    public bool? Resolved { get; set; }
}
