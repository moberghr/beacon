using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.DTOs;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskCommentsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetTaskCommentsQuery, TaskCommentsResult>
{
    public async Task<TaskCommentsResult> Handle(GetTaskCommentsQuery request, CancellationToken cancellationToken)
    {
        var comments = await Read(request.TaskId, cancellationToken);

        var items = comments
            .Select(x => new TaskCommentItem(x.Id, x.Content, x.UserName, x.CreatedAt))
            .ToList();

        return new TaskCommentsResult(request.TaskId, items);
    }

    internal async Task<List<CommentData>> Read(int taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Comments
            .Where(x => x.EntityType == EntityType.Task)
            .Where(x => x.EntityId == taskId)
            .OrderByDescending(x => x.CreatedTime)
            .Select(x => new CommentData(x.Id, x.Content, x.UserName, x.CreatedTime))
            .ToListAsync(cancellationToken);
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
