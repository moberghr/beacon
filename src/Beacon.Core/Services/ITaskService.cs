using Beacon.Core.DTOs;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Tasks;

namespace Beacon.Core.Services;

public interface ITaskService
{
    Task<int> CreateTask(int notificationId, int subscriptionId, int resultCount, CancellationToken cancellationToken);
    Task<int> CreateOrUpdateTask(int subscriptionId, int resultCount, CancellationToken cancellationToken);
    /// <summary>Resolves an open task, recording <paramref name="userId"/> as the resolver. A resolved task is never re-resolved: it throws.</summary>
    Task ResolveTask(int taskId, string? resolutionNotes, string? userId, CancellationToken cancellationToken);
    Task ReopenTask(int taskId, CancellationToken cancellationToken);
    Task<PagedList<TaskData>> GetTasks(GetTasksRequest request, CancellationToken cancellationToken);
    Task<TaskDetailsData?> GetTaskDetails(int taskId, string? currentUserId, CancellationToken cancellationToken);
    Task<TaskStatisticsData> GetTaskStatistics(CancellationToken cancellationToken);

    // Execution history for task
    Task<List<QueryExecutionSummary>> GetTaskExecutionHistory(int taskId, CancellationToken cancellationToken);

    // Related tasks (tasks from same query)
    Task<List<RelatedTaskSummary>> GetRelatedTasks(int taskId, CancellationToken cancellationToken);

    // Result count chart data
    Task<List<ResultCountDataPoint>> GetResultCountHistory(int taskId, CancellationToken cancellationToken);

    // Comments
    Task<List<CommentData>> GetTaskComments(int taskId, CancellationToken cancellationToken);
    Task<int> AddTaskComment(int taskId, string content, string? userId, string? userName, CancellationToken cancellationToken);
}
