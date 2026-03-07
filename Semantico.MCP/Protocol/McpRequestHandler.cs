using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Semantico.Core.Services;
using Semantico.MCP.Resources;
using Semantico.MCP.Services;
using Semantico.MCP.Tools;

namespace Semantico.MCP.Protocol;

internal sealed class McpRequestHandler(
    IEnumerable<IMcpTool> tools,
    IEnumerable<IMcpResource> resources,
    IMcpSettingsProvider settingsProvider,
    McpAuditService auditService,
    ILogger<McpRequestHandler> logger)
{
    public async Task<JsonRpcResponse> HandleRequestAsync(JsonRpcRequest request, McpClientSession session, CancellationToken ct)
    {
        try
        {
            var result = request.Method switch
            {
                "initialize" => HandleInitialize(session),
                "initialized" => HandleInitialized(),
                "ping" => HandlePing(),
                "tools/list" => await HandleListToolsAsync(),
                "tools/call" => await HandleToolCallAsync(request.Params, session, ct),
                "resources/list" => await HandleListResourcesAsync(ct),
                "resources/read" => await HandleReadResourceAsync(request.Params, session, ct),
                _ => throw new McpMethodNotFoundException($"Unknown method: {request.Method}")
            };

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = result
            };
        }
        catch (McpMethodNotFoundException ex)
        {
            logger.LogWarning("Unknown MCP method: {Method}", request.Method);
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError { Code = -32601, Message = ex.Message }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling MCP request: {Method}", request.Method);
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError { Code = -32603, Message = ex.Message }
            };
        }
    }

    private static object HandleInitialize(McpClientSession session)
    {
        session.IsInitialized = true;
        return new McpInitializeResult();
    }

    private static object HandleInitialized() => new { };

    private static object HandlePing() => new { };

    private async Task<object> HandleListToolsAsync()
    {
        var settings = await settingsProvider.GetSettingsAsync();

        return new McpListToolsResult
        {
            Tools = tools.Select(t => new McpTool
            {
                Name = t.Name,
                Description = GetToolDescription(t, settings),
                InputSchema = t.InputSchema
            }).ToList()
        };
    }

    private static string GetToolDescription(IMcpTool tool, Core.Models.McpSettingsData settings)
    {
        var custom = tool.Name switch
        {
            "list_datasources" => settings.ListDataSourcesDescription,
            "query" => settings.QueryDescription,
            "get_documentation" => settings.GetDocumentationDescription,
            "ask" => settings.AskDescription,
            _ => null
        };
        return !string.IsNullOrWhiteSpace(custom) ? custom : tool.Description;
    }

    private async Task<object> HandleToolCallAsync(JsonElement? parameters, McpClientSession session, CancellationToken ct)
    {
        if (parameters == null)
            return new McpToolResult { Content = [new McpContent { Text = "Missing parameters" }], IsError = true };

        var toolName = parameters.Value.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var arguments = parameters.Value.TryGetProperty("arguments", out var argsProp) ? argsProp : (JsonElement?)null;

        if (string.IsNullOrEmpty(toolName))
            return new McpToolResult { Content = [new McpContent { Text = "Missing tool name" }], IsError = true };

        var tool = tools.FirstOrDefault(t => t.Name == toolName);
        if (tool == null)
            return new McpToolResult { Content = [new McpContent { Text = $"Unknown tool: {toolName}" }], IsError = true };

        logger.LogInformation("Executing MCP tool: {Tool} for session {Session}", toolName, session.SessionId);

        var dbSession = await auditService.GetOrCreateSessionAsync(session.SessionId, session.UserId, session.ApiKeyId, ct);
        var argsJson = arguments?.GetRawText();
        var dataSourceId = arguments.HasValue ? ToolHelper.GetInt(arguments, "datasource_id") : null;
        var sw = Stopwatch.StartNew();
        string? errorMessage = null;
        int? resultRowCount = null;
        McpToolResult result;

        try
        {
            result = await tool.ExecuteAsync(arguments, session, ct);
            if (result.IsError)
                errorMessage = result.Content?.FirstOrDefault()?.Text;
        }
        catch (Exception ex)
        {
            sw.Stop();
            errorMessage = ex.Message;
            _ = auditService.LogToolCallAsync(dbSession?.Id, session.UserId, toolName, argsJson,
                dataSourceId, (int)sw.ElapsedMilliseconds, null, errorMessage, ct);
            throw;
        }

        sw.Stop();
        await auditService.LogToolCallAsync(dbSession?.Id, session.UserId, toolName, argsJson,
            dataSourceId, (int)sw.ElapsedMilliseconds, resultRowCount, errorMessage, ct);
        await auditService.UpdateSessionActivityAsync(session.SessionId, ct);

        return result;
    }

    private async Task<object> HandleListResourcesAsync(CancellationToken ct)
    {
        var resourceList = new List<McpResource>();
        foreach (var resource in resources)
            resourceList.AddRange(await resource.ListAsync(ct));
        return new McpListResourcesResult { Resources = resourceList };
    }

    private async Task<object> HandleReadResourceAsync(JsonElement? parameters, McpClientSession session, CancellationToken ct)
    {
        if (parameters == null)
            return new McpReadResourceResult();

        var uri = parameters.Value.TryGetProperty("uri", out var uriProp) ? uriProp.GetString() : null;
        if (string.IsNullOrEmpty(uri))
            return new McpReadResourceResult();

        foreach (var resource in resources)
        {
            if (resource.CanHandle(uri))
            {
                var content = await resource.ReadAsync(uri, ct);
                if (content != null)
                    return new McpReadResourceResult { Contents = [content] };
            }
        }

        return new McpReadResourceResult();
    }
}

internal class McpMethodNotFoundException(string message) : Exception(message);
