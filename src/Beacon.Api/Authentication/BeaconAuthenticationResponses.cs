using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Beacon.Core.Mcp;

namespace Beacon.Api.Authentication;

/// <summary>
/// The one shape of Beacon's authentication answers: an RFC 7807 problem (<c>application/problem+json</c>) for a refused
/// API key, a refused bearer token, an unauthenticated or forbidden API request, and an unreachable signing-key endpoint.
/// A bearer refusal also carries its OAuth <c>error</c> code and its <c>WWW-Authenticate</c> challenge.
/// </summary>
internal static class BeaconAuthenticationResponses
{
    private static readonly JsonSerializerOptions ProblemJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Task WriteRefusalAsync(HttpContext context, BeaconCredentialRefusal refusal)
    {
        if (IsThrottled(context, refusal.StatusCode, out var throttled))
        {
            return throttled;
        }

        if (refusal.Challenge != null)
        {
            context.Response.Headers.WWWAuthenticate = refusal.Challenge;
        }

        if (refusal.RetryAfter != null)
        {
            context.Response.Headers.RetryAfter = refusal.RetryAfter;
        }

        // A bearer refusal keeps the members its body had before it was a problem (error, message) for 5.x clients.
        var extensions = refusal.Error == null
            ? null
            : new Dictionary<string, object?> { ["error"] = refusal.Error, ["message"] = refusal.Message };

        return WriteProblemCoreAsync(context, refusal.StatusCode, refusal.Message, extensions);
    }

    public static Task WriteProblemAsync(HttpContext context, int statusCode, string? detail = null, IDictionary<string, object?>? extensions = null)
    {
        return IsThrottled(context, statusCode, out var throttled)
            ? throttled
            : WriteProblemCoreAsync(context, statusCode, detail, extensions);
    }

    // A 401 is counted against the client address's limit of refused anonymous requests (Beacon:RateLimits:Anonymous):
    // over it, 429 instead of the 401.
    private static bool IsThrottled(HttpContext context, int statusCode, out Task refusal)
    {
        if (statusCode == StatusCodes.Status401Unauthorized && !AnonymousRefusalThrottle.TryAdmit(context, out var retryAfter))
        {
            refusal = RateLimitRefusal.WriteAsync(context, retryAfter);
            return true;
        }

        refusal = Task.CompletedTask;
        return false;
    }

    // Written without the request's services (the framework's problem writer needs them), so a bare pipeline answers too.
    private static Task WriteProblemCoreAsync(HttpContext context, int statusCode, string? detail, IDictionary<string, object?>? extensions)
    {
        var problem = new ProblemDetails
        {
            Type = statusCode switch
            {
                StatusCodes.Status401Unauthorized => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                StatusCodes.Status403Forbidden => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                _ => "https://tools.ietf.org/html/rfc9110#section-15.6.4"
            },
            Title = statusCode switch
            {
                StatusCodes.Status401Unauthorized => "Unauthorized",
                StatusCodes.Status403Forbidden => "Forbidden",
                _ => "Service Unavailable"
            },
            Status = statusCode,
            Detail = detail
        };
        foreach (var (key, value) in extensions ?? new Dictionary<string, object?>())
        {
            problem.Extensions[key] = value;
        }

        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        context.Response.StatusCode = statusCode;

        return context.Response.WriteAsJsonAsync(problem, ProblemJson, "application/problem+json", context.RequestAborted);
    }
}

/// <summary>
/// The one test of whether a request is an API call, answered with a status and a problem body instead of the login
/// redirect: the MCP route (MCP clients do not always send <c>Accept: application/json</c>, and cannot follow a redirect),
/// Beacon's REST API, any request accepting JSON, and an <c>/api/</c> path among Beacon's routes
/// (<see cref="BeaconRequestPaths"/>, e.g. a listed dashboard's own API).
/// </summary>
internal static class BeaconApiRequest
{
    private const string ApiPath = "/beacon/api";

    public static bool Is(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments(McpDiscoveryPaths.McpPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments(ApiPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var accept = context.Request.Headers.Accept.ToString();
        if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Value?.Contains("/api/", StringComparison.OrdinalIgnoreCase) == true && BeaconRequestPaths.IsBeaconRequest(context);
    }
}
