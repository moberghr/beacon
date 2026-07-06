using System.Text.Json;
using Beacon.Core.Models;

namespace Beacon.SampleProject.Middleware;

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
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden", "/errors/forbidden"),
        InvalidOperationException ioe => (StatusCodes.Status400BadRequest, ioe.Message, "/errors/invalid-operation"),
        BeaconException be => (StatusCodes.Status400BadRequest, be.Message, "/errors/beacon"),
        _ => (StatusCodes.Status500InternalServerError, "Internal server error", "/errors/internal"),
    };
}

internal sealed record ApiProblem(string Type, string Title, int Status);

internal static class ApiExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseApiExceptionHandler(this IApplicationBuilder app, PathString pathPrefix)
    {
        return app.UseWhen(
            context => context.Request.Path.StartsWithSegments(pathPrefix),
            branch => branch.UseMiddleware<ApiExceptionMiddleware>());
    }
}
