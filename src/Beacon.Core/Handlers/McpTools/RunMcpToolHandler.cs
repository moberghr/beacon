using MediatR;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Mcp.RunMcpTool;

internal sealed class RunMcpToolHandler(
    IMcpPlaygroundService playgroundService)
    : IRequestHandler<RunMcpToolCommand, RunMcpToolResult>
{
    public async Task<RunMcpToolResult> Handle(RunMcpToolCommand request, CancellationToken cancellationToken)
    {
        var arguments = request.Arguments ?? new Dictionary<string, object?>();
        var result = await playgroundService.ExecuteToolAsync(request.ToolName, arguments, request.ProjectId, cancellationToken);
        return new RunMcpToolResult(result.Text, result.IsError);
    }
}

public record RunMcpToolCommand(
    string ToolName,
    int ProjectId,
    Dictionary<string, object?>? Arguments) : IRequest<RunMcpToolResult>;

public record RunMcpToolResult(string Text, bool IsError);

public record GetMcpToolsQuery : IRequest<GetMcpToolsResult>;
public record GetMcpToolsResult(IReadOnlyList<string> ToolNames);

internal sealed class GetMcpToolsHandler(
    IMcpPlaygroundService playgroundService)
    : IRequestHandler<GetMcpToolsQuery, GetMcpToolsResult>
{
    public Task<GetMcpToolsResult> Handle(GetMcpToolsQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new GetMcpToolsResult(playgroundService.ToolNames));
    }
}
