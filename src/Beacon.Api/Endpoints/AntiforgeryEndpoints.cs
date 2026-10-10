using Beacon.Api.Authentication;
using Microsoft.AspNetCore.Antiforgery;

namespace Beacon.Api.Endpoints;

internal static class AntiforgeryEndpoints
{
    public static RouteGroupBuilder MapAntiforgeryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/csrf", IssueCsrfToken)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("IssueCsrfToken")
            .WithTags("Auth");

        return group;
    }

    private static IResult IssueCsrfToken(IAntiforgery antiforgery, HttpContext httpContext)
    {
        // Tokens are for browser sessions. A caller authenticated from its Authorization header is never
        // antiforgery-checked, so it gets no token (and no antiforgery cookie) bound to its key or bearer identity.
        if (HeaderAuthenticatedRequest.Is(httpContext))
        {
            return Results.Problem(
                title: "Antiforgery tokens are issued to browser sessions only.",
                detail: "Requests authenticated with an Authorization header do not need an antiforgery token.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        var token = tokens.RequestToken ?? string.Empty;

        httpContext.Response.Cookies.Append(
            "XSRF-TOKEN",
            token,
            new CookieOptions
            {
                HttpOnly = false,
                Secure = httpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
            });

        return Results.Ok(new CsrfTokenResponse(token));
    }
}

internal sealed record CsrfTokenResponse(string Token);
