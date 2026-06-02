using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Beacon.SampleProject.Authentication;

internal sealed class BasicAuthMiddleware(
    RequestDelegate next,
    BasicAuthConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!configuration.Enabled)
        {
            await next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        if (authHeader == null || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            await ChallengeAsync(context);
            return;
        }

        var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
        var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
        var parts = credentials.Split(':', 2);

        if (parts.Length != 2)
        {
            await ChallengeAsync(context);
            return;
        }

        var username = parts[0];
        var password = parts[1];

        if (username == configuration.Username && password == configuration.Password)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.NameIdentifier, username)
            };
            var identity = new ClaimsIdentity(claims, "BasicAuthentication");
            context.User = new ClaimsPrincipal(identity);

            await next(context);
            return;
        }

        await ChallengeAsync(context);
    }

    private Task ChallengeAsync(HttpContext context)
    {
        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{configuration.Realm}\"";
        return Task.CompletedTask;
    }
}
