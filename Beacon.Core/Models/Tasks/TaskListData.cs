using Beacon.Core.DTOs;
using Beacon.Core.Helpers;

namespace Beacon.Core.Models.Tasks;

public class TaskListData : IPagedListResponse<TaskData>
{
    public List<TaskData> Data { get; set; } = new();
    public int? TotalCount { get; set; }
}
