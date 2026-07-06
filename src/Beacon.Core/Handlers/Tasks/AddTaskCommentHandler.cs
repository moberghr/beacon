using Beacon.Core.Authorization;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class AddTaskCommentHandler(
    ITaskService taskService,
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

        var commentId = await taskService.AddTaskComment(request.TaskId, content, userId, userName, cancellationToken);

        return new AddTaskCommentResult(commentId);
    }
}

public record AddTaskCommentCommand(int TaskId, string Content) : IRequest<AddTaskCommentResult>;

public record AddTaskCommentResult(int Id);
