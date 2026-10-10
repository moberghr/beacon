using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskCommentsHandler(ITaskService taskService)
    : IRequestHandler<GetTaskCommentsQuery, TaskCommentsResult>
{
    public async Task<TaskCommentsResult> Handle(GetTaskCommentsQuery request, CancellationToken cancellationToken)
    {
        var comments = await taskService.GetTaskComments(request.TaskId, cancellationToken);

        var items = comments
            .Select(x => new TaskCommentItem(x.Id, x.Content, x.UserName, x.CreatedAt))
            .ToList();

        return new TaskCommentsResult(request.TaskId, items);
    }
}

public record GetTaskCommentsQuery(int TaskId) : IRequest<TaskCommentsResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Task(TaskId), ProjectAction.Read)
    ];
}

public record TaskCommentsResult(int TaskId, IReadOnlyList<TaskCommentItem> Comments);

public record TaskCommentItem(int Id, string Content, string? UserName, DateTime CreatedAt);
