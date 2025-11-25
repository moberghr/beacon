using Semantico.Core.Helpers;

namespace Semantico.Core.Models.Tasks;

public class GetTasksRequest : SortedListRequest
{
    public int? SubscriptionId { get; set; }
    public bool? Resolved { get; set; }
}
