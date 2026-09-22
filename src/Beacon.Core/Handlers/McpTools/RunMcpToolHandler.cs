using MediatR;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Mcp.RunMcpTool;

internal sealed class RunMcpToolHandler(
    IMcpPlaygroundService? playgroundService = null)
    : IRequestHandler<RunMcpToolCommand, RunMcpToolResult>
{
    public async Task<RunMcpToolResult> Handle(RunMcpToolCommand request, CancellationToken cancellationToken)
    {
        if (playgroundService == null)
        {
            throw new InvalidOperationException(McpDisabled.Message);
        }

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
    IMcpPlaygroundService? playgroundService = null)
    : IRequestHandler<GetMcpToolsQuery, GetMcpToolsResult>
{
    public Task<GetMcpToolsResult> Handle(GetMcpToolsQuery request, CancellationToken cancellationToken)
    {
        if (playgroundService == null)
        {
            throw new InvalidOperationException(McpDisabled.Message);
        }

        return Task.FromResult(new GetMcpToolsResult(playgroundService.ToolNames));
    }
}

/// <summary>
/// Core scans its MediatR handlers in by unconditional assembly scanning, but the only real
/// <see cref="IMcpPlaygroundService"/> lives in Beacon.MCP and is wired by <c>AddBeaconMcp()</c>.
/// Injecting it optionally keeps a host that never calls <c>AddBeaconMcp()</c> able to pass DI
/// validation; reaching one of these handlers anyway is a real misconfiguration, so it throws
/// rather than returning an empty result that would read as "this host has no MCP tools".
///
/// Optional injection rather than a Core-side fallback registration on purpose: <c>TryAdd</c> is
/// first-wins, so a stand-in registered by Core would permanently shadow the real implementation
/// whenever <c>AddBeaconMcp()</c> runs afterwards.
/// </summary>
internal static class McpDisabled
{
    public const string Message =
        "This Beacon host does not have the MCP layer registered, so MCP playground tools are "
        + "unavailable. Call AddBeaconMcp() during startup to enable them.";
}
