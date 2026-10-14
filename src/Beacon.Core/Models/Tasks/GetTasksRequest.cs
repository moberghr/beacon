using Beacon.Core.Authorization;
using Beacon.Core.Helpers;

namespace Beacon.Core.Models.Tasks;

public record GetTasksRequest : ListRequest
{
    /// <summary>
    /// Where the caller may read: a task is listed as its subscription's query. Required, so no caller lists every task
    /// by leaving it out; <see cref="ProjectSet.All"/> is an explicit choice.
    /// </summary>
    public required ProjectSet Readable { get; init; }

    public int? SubscriptionId { get; init; }

    public bool? Resolved { get; init; }
}
