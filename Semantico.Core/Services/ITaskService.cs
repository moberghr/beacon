using Semantico.Core.DTOs;
using Semantico.Core.Helpers;
using Semantico.Core.Models.Tasks;

namespace Semantico.Core.Services;

public interface ITaskService
{
    Task<int> CreateTask(int notificationId, int subscriptionId, int resultCount, CancellationToken cancellationToken);
    Task<int> CreateOrUpdateTask(int subscriptionId, int resultCount, CancellationToken cancellationToken);
    Task ResolveTask(int taskId, string? resolutionNotes, string? userId, CancellationToken cancellationToken);
    Task ReopenTask(int taskId, CancellationToken cancellationToken);
    Task<TaskListData> GetTasks(GetTasksRequest request, CancellationToken cancellationToken);
    Task<TaskDetailsData?> GetTaskDetails(int taskId, CancellationToken cancellationToken);
    Task<TaskStatisticsData> GetTaskStatistics(CancellationToken cancellationToken);
}
