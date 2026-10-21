using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Api.Authentication;

namespace Beacon.Gateway;

/// <summary>
/// The MCP route's authentication scheme. It authenticates nobody itself: it answers with the principal Beacon's bearer
/// middleware mapped on this request (<see cref="McpCallerAuthenticationFeature"/>), so the route's policy is evaluated
/// against the MCP credential and never against a host cookie that authenticated the same request. Its challenge is a
/// 401 with a bearer challenge (which the gateway's pipeline extends with RFC 9728 <c>resource_metadata</c>), and its
/// forbid a 403 — never the host's sign-in redirect.
/// </summary>
internal sealed class BeaconMcpAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = Context.Features.Get<McpCallerAuthenticationFeature>()?.Principal;

        return Task.FromResult(principal?.Identity?.IsAuthenticated == true
            ? AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name))
            : AuthenticateResult.NoResult());
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";

        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    }
}
