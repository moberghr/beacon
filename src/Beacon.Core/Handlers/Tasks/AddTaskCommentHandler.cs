using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class AddTaskCommentHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext)
    : IRequestHandler<AddTaskCommentCommand, AddTaskCommentResult>
{
    public async Task<AddTaskCommentResult> Handle(AddTaskCommentCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new InvalidOperationException("Comment content cannot be empty.");
        }

        var content = request.Content.Trim();
        var userId = userContext.IsAuthenticated ? userContext.UserId : null;
        var userName = userContext.IsAuthenticated
            ? (userContext.DisplayName ?? userContext.UserName)
            : null;

        var commentId = await Add(request.TaskId, content, userId, userName, cancellationToken);

        return new AddTaskCommentResult(commentId);
    }

    internal async Task<int> Add(int taskId, string content, string? userId, string? userName, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var taskExists = await context.QueryTasks
            .Where(x => x.Id == taskId)
            .AnyAsync(cancellationToken);

        if (!taskExists)
        {
            throw new BeaconException($"Task {taskId} not found");
        }

        var comment = new Comment
        {
            EntityType = EntityType.Task,
            EntityId = taskId,
            Content = content,
            UserId = userId,
            UserName = userName
        };

        context.Comments.Add(comment);
        await context.SaveChangesAsync(cancellationToken);

        return comment.Id;
    }
}

public record AddTaskCommentCommand(int TaskId, string Content) : IRequest<AddTaskCommentResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Task(TaskId), ProjectAction.Write)
    ];
}

public record AddTaskCommentResult(int Id);
