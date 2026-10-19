using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Beacon.MCP.Services;

/// <summary>
/// Fills the request's <see cref="IProjectContext"/> from the caller's project access
/// (<see cref="ProjectContextFactory.PopulateAsync"/>) before anything else handles a <c>tools/call</c> or a
/// <c>tools/list</c>: the attribute tools, the saved-query and host endpoint tools, and the scope filter's audit of a
/// refused call all read it. Each MCP request has its own DI scope, so the access is decided per call and a change (a
/// revoked membership, a disabled user) applies to the next call of the same session. When the access could not be
/// determined (<see cref="IProjectContext.AccessUndetermined"/>), a <c>tools/list</c> fails instead of listing fewer
/// tools, and a <c>tools/call</c> that passed the scope check is answered with a retry message and audited
/// (<see cref="CreateUndeterminedAccessCallToolFilter"/>).
/// </summary>
internal static class McpProjectContextFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateCallToolFilter()
    {
        return next => async (request, cancellationToken) =>
        {
            if (request.Services != null)
            {
                await ProjectContextFactory.PopulateAsync(request.Services, cancellationToken);
            }

            return await next(request, cancellationToken);
        };
    }

    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> CreateListToolsFilter()
    {
        return next => async (request, cancellationToken) =>
        {
            if (request.Services != null)
            {
                await ProjectContextFactory.PopulateAsync(request.Services, cancellationToken);
                if (request.Services.GetService<McpProjectContext>()?.AccessUndetermined == true)
                {
                    throw new McpProtocolException(ProjectContextFactory.AccessUndeterminedMessage, McpErrorCode.InternalError);
                }
            }

            return await next(request, cancellationToken);
        };
    }

    /// <summary>
    /// Answers a call whose caller's project access could not be determined with a retry message, before any tool (an
    /// attribute tool, a saved-query or host endpoint tool) runs, and audits it (§1.7). Registered inside the scope
    /// filter, so a caller the scope check refuses gets that refusal.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateUndeterminedAccessCallToolFilter()
    {
        return next => async (request, cancellationToken) =>
        {
            var context = request.Services?.GetService<McpProjectContext>();
            if (context?.AccessUndetermined != true)
            {
                return await next(request, cancellationToken);
            }

            var audit = request.Services!.GetService<McpAuditService>();
            if (audit != null)
            {
                await audit.LogToolCallAsync(
                    userId: context.UserId,
                    tool: request.Params?.Name ?? McpAuditService.InvalidToolName,
                    parameters: null,
                    dataSourceId: null,
                    projectId: null,
                    executionTimeMs: 0,
                    resultRowCount: null,
                    errorMessage: ProjectContextFactory.AccessUndeterminedMessage,
                    ct: cancellationToken);
            }

            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = ProjectContextFactory.AccessUndeterminedMessage }]
            };
        };
    }
}
