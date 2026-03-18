using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Semantico.MCP.Protocol;

namespace Semantico.MCP;

public static class McpEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static IEndpointRouteBuilder MapSemanticoMcp(this IEndpointRouteBuilder endpoints, string basePath = "/semantico/mcp")
    {
        // SSE endpoint - client connects here to receive server messages
        endpoints.MapGet($"{basePath}/sse", HandleSseAsync);

        // Message endpoint - client sends JSON-RPC messages here
        endpoints.MapPost($"{basePath}/message", HandleMessageAsync);

        return endpoints;
    }

    private static async Task HandleSseAsync(HttpContext context)
    {
        // Require authentication — MCP clients must provide a valid API key or cookie
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Authentication required. Provide a valid API key via Bearer token.");
            return;
        }

        var sessionManager = context.RequestServices.GetRequiredService<McpSessionManager>();
        var logger = context.RequestServices.GetRequiredService<ILogger<McpSessionManager>>();

        // Get user identity from auth
        var userId = GetUserId(context);
        var apiKeyId = GetApiKeyId(context);

        var session = sessionManager.CreateSession(userId, apiKeyId);
        session.AllowedProjectIds = GetAllowedProjectIds(context);

        logger.LogInformation("MCP SSE connection opened: {SessionId} (User: {UserId})", session.SessionId, userId);

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        // Send the endpoint URL as the first message so the client knows where to POST
        var ssePathValue = context.Request.Path.Value!;
        var messageEndpoint = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{ssePathValue.Replace("/sse", "/message")}?sessionId={session.SessionId}";
        await context.Response.WriteAsync($"event: endpoint\ndata: {messageEndpoint}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);

        try
        {
            // Keep connection alive, forwarding messages from the session channel
            await foreach (var message in session.Messages.ReadAllAsync(context.RequestAborted))
            {
                await context.Response.WriteAsync(message, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        finally
        {
            logger.LogInformation("MCP SSE connection closed: {SessionId}", session.SessionId);
            await sessionManager.RemoveSessionAsync(session.SessionId);
        }
    }

    private static async Task HandleMessageAsync(HttpContext context)
    {
        // Require authentication
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Authentication required.");
            return;
        }

        var sessionManager = context.RequestServices.GetRequiredService<McpSessionManager>();
        var requestHandler = context.RequestServices.GetRequiredService<McpRequestHandler>();
        var logger = context.RequestServices.GetRequiredService<ILogger<McpRequestHandler>>();

        var sessionId = context.Request.Query["sessionId"].FirstOrDefault();
        if (string.IsNullOrEmpty(sessionId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing sessionId query parameter");
            return;
        }

        var session = sessionManager.GetSession(sessionId);
        if (session == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Session not found. Connect to the SSE endpoint first.");
            return;
        }

        // Verify the caller owns this session (prevent session hijacking)
        var callerApiKeyId = GetApiKeyId(context);
        var callerUserId = GetUserId(context);
        if (session.ApiKeyId != callerApiKeyId || session.UserId != callerUserId)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Access denied: session belongs to a different identity.");
            return;
        }

        // Read request body
        JsonRpcRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(
                context.Request.Body, JsonOptions, context.RequestAborted);
        }
        catch (JsonException ex)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"Invalid JSON: {ex.Message}");
            return;
        }

        if (request == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Empty request");
            return;
        }

        // Handle the request
        var response = await requestHandler.HandleRequestAsync(request, session, context.RequestAborted);

        // Send response through SSE channel
        await session.SendMessageAsync(response, context.RequestAborted);

        // Also return 202 Accepted with empty body (MCP SSE pattern)
        context.Response.StatusCode = 202;
        await context.Response.WriteAsync("Accepted");
    }

    private static int? GetUserId(HttpContext context)
    {
        var claim = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
    }

    private static int? GetApiKeyId(HttpContext context)
    {
        var claim = context.User?.FindFirst("api_key_id");
        return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
    }

    private static List<int>? GetAllowedProjectIds(HttpContext context)
    {
        var claim = context.User?.FindFirst("allowed_projects");
        if (claim == null || string.IsNullOrEmpty(claim.Value)) return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<int>>(claim.Value);
        }
        catch
        {
            return null;
        }
    }
}
