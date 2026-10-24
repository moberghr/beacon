using System.Text.Json;
using Beacon.Api.Endpoints;
using Beacon.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Beacon.Api.Middleware;

internal sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; nothing useful to write.
            throw;
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            await WriteAsync(context, ex);
        }
    }

    private async Task WriteAsync(HttpContext context, Exception ex)
    {
        var (status, title, type) = Map(ex);

        if (status >= 500)
        {
            logger.LogError(ex, "Unhandled exception in {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        else
        {
            logger.LogInformation("Mapped {Exception} to HTTP {Status} for {Method} {Path}",
                ex.GetType().Name, status, context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        var payload = new ApiProblem(type, title, status);
        await JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonOptions, context.RequestAborted);
    }

    private static (int Status, string Title, string Type) Map(Exception ex) => ex switch
    {
        UnauthorizedAccessException or BeaconForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden", "/errors/forbidden"),
        InvalidOperationException ioe => (StatusCodes.Status400BadRequest, ioe.Message, "/errors/invalid-operation"),
        // Before the BeaconException arm — a write that contradicts a deployment lock is a conflict, not a bad request.
        SettingLockedException sle => (StatusCodes.Status409Conflict, sle.Message, "/errors/setting-locked"),
        BeaconException be => (StatusCodes.Status400BadRequest, be.Message, "/errors/beacon"),
        _ => (StatusCodes.Status500InternalServerError, "Internal server error", "/errors/internal"),
    };
}

internal sealed record ApiProblem(string Type, string Title, int Status);

public static class ApiExceptionMiddlewareExtensions
{
    /// <summary>
    /// Turns an exception thrown by a request under <c>/beacon/api</c> into an RFC 7807 <c>application/problem+json</c>
    /// response: <see cref="InvalidOperationException"/> and <see cref="BeaconException"/> as 400 with their message,
    /// <see cref="SettingLockedException"/> as 409, <see cref="BeaconForbiddenException"/> and
    /// <see cref="UnauthorizedAccessException"/> as 403, anything else as a 500 without details (logged). Other paths are
    /// not touched, and neither is a response that has started or a request the client cancelled. <c>UseBeaconPipeline()</c>
    /// adds it first; a host wiring Beacon's middlewares itself calls it after <c>UseStaticFiles</c> and before
    /// <c>UseBeaconAuthentication</c>, so the exceptions of authentication, antiforgery and the redirects are mapped too.
    /// </summary>
    public static IApplicationBuilder UseBeaconApiExceptionHandler(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseApiExceptionHandler(BeaconApiEndpoints.GroupPrefix);
    }

    internal static IApplicationBuilder UseApiExceptionHandler(this IApplicationBuilder app, PathString pathPrefix)
    {
        return app.UseWhen(
            context => context.Request.Path.StartsWithSegments(pathPrefix),
            branch => branch.UseMiddleware<ApiExceptionMiddleware>());
    }
}
