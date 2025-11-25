using Semantico.Core.DTOs;
using Semantico.Core.Helpers;

namespace Semantico.Core.Models.Tasks;

public class TaskListData : IPagedListResponse<TaskData>
{
    public List<TaskData> Data { get; set; } = new();
    public int? TotalCount { get; set; }
}
