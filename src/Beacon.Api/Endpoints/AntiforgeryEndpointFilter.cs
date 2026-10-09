using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beacon.Api.Endpoints;

/// <summary>
/// Validates the antiforgery token for non-GET/HEAD/OPTIONS requests from
/// authenticated users, and from anonymous callers too on an endpoint that carries
/// <see cref="AntiforgeryForAnonymousCallers"/>. Anonymous endpoints opt out via <c>.DisableAntiforgery()</c>.
/// </summary>
internal sealed class AntiforgeryEndpointFilter(
    IAntiforgery antiforgery,
    IOptions<AntiforgeryOptions> antiforgeryOptions,
    ILogger<AntiforgeryEndpointFilter> logger) : IEndpointFilter
{
    /// <summary>
    /// The header the React shell sends the token in (<c>web/src/lib/csrf.ts</c>). Antiforgery options are
    /// host-global, so a host that keeps the default <c>RequestVerificationToken</c> (for its own MVC/AJAX)
    /// would otherwise reject every SPA mutation.
    /// </summary>
    internal const string SpaHeaderName = "X-XSRF-TOKEN";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var method = httpContext.Request.Method;

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return await next(context);
        }

        if (httpContext.User.Identity?.IsAuthenticated != true && !ValidatesAnonymousCallers(httpContext))
        {
            return await next(context);
        }

        CopySpaHeaderToConfiguredHeader(httpContext.Request);

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException ex)
        {
            logger.LogWarning(ex, "Antiforgery validation failed for {Method} {Path}", method, httpContext.Request.Path);
            return Results.Problem(
                title: "Invalid antiforgery token.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }

    private static bool ValidatesAnonymousCallers(HttpContext httpContext)
    {
        return httpContext.GetEndpoint()?.Metadata.GetMetadata<AntiforgeryForAnonymousCallers>() != null;
    }

    // Only the header NAME is bridged; the token itself is still validated against the host's
    // antiforgery cookie and the caller's identity, so this does not weaken the check.
    private void CopySpaHeaderToConfiguredHeader(HttpRequest request)
    {
        var headerName = antiforgeryOptions.Value.HeaderName;
        if (headerName == null || string.Equals(headerName, SpaHeaderName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (request.Headers.ContainsKey(headerName) || !request.Headers.TryGetValue(SpaHeaderName, out var token))
        {
            return;
        }

        request.Headers[headerName] = token;
    }
}

/// <summary>
/// Endpoint metadata: <see cref="AntiforgeryEndpointFilter"/> validates the token on this endpoint even when the caller
/// is anonymous. For an anonymous endpoint whose answer changes browser state without a session, such as sign-out,
/// which emits cookie deletions: the session cookie is withheld from a cross-site request, so "anonymous" says nothing
/// about where the request came from.
/// </summary>
internal sealed class AntiforgeryForAnonymousCallers
{
    public static readonly AntiforgeryForAnonymousCallers Instance = new();
}
