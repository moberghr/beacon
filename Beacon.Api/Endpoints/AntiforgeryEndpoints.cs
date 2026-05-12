using Microsoft.AspNetCore.Antiforgery;

namespace Beacon.Api.Endpoints;

internal static class AntiforgeryEndpoints
{
    public static RouteGroupBuilder MapAntiforgeryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/csrf", IssueCsrfToken)
            .AllowAnonymous()
            .WithName("IssueCsrfToken")
            .WithTags("Auth");

        return group;
    }

    private static IResult IssueCsrfToken(IAntiforgery antiforgery, HttpContext httpContext)
    {
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
