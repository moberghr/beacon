using System.Text.Json;
using Beacon.Api.Endpoints;
using Beacon.Core.Helpers;
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
            // The stack's frames name types and methods only: never the message, an argument or a file path (§1.11).
            logger.LogError(
                "Unhandled exception in {Method} {Path} ({Failure}); stack: {Stack}",
                RequestLogText.Method(context.Request),
                RequestLogText.Path(context.Request),
                FailureCodes.Describe(ex),
                FailureCodes.StackOf(ex));
        }
        else
        {
            logger.LogInformation("Mapped {Exception} to HTTP {Status} for {Method} {Path}",
                ex.GetType().Name, status, RequestLogText.Method(context.Request), RequestLogText.Path(context.Request));
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        var payload = new ApiProblem(type, title, status);
        await JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonOptions, context.RequestAborted);
    }

    private static (int Status, string Title, string Type) Map(Exception ex) => ex switch
    {
        UnauthorizedAccessException or BeaconForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden", "/errors/forbidden"),
        // Only Beacon's own business-rule text reaches the caller; a library's InvalidOperationException is an internal error.
        InvalidOperationException ioe when BeaconOwnErrors.IsThrownByBeacon(ioe) => (StatusCodes.Status400BadRequest, ioe.Message, "/errors/invalid-operation"),
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
    /// response: an <see cref="InvalidOperationException"/> thrown by Beacon's code and a <see cref="BeaconException"/> as
    /// 400 with their message, <see cref="SettingLockedException"/> as 409, <see cref="BeaconForbiddenException"/> and
    /// <see cref="UnauthorizedAccessException"/> as 403, anything else (a library's <see cref="InvalidOperationException"/>
    /// included) as a 500 without details, logged by its type, code and stack frames (type and method names) only. Other paths are
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
