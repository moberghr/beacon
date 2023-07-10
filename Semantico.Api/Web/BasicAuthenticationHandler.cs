using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Semantico.Api.Helpers;
using Semantico.Api.Services;

namespace Semantico.Api.Web;

public class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IAccountService _accountService;

    public BasicAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock, IAccountService accountService)
        : base(options, logger, encoder, clock)
    {
        _accountService = accountService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            var authHeader = Request.Headers.Authorization.ToString() ?? string.Empty;

            if (!authHeader.StartsWith("basic", StringComparison.OrdinalIgnoreCase))
            {
                return AuthenticateResult.Fail("Missing Authorization Header.");
            }

            var token = authHeader["Basic ".Length..].Trim();
            var credentialString = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var credentials = credentialString.Split(':');

            var account = await _accountService.GetAccount(credentials[0]);

            if (PasswordHasher.Check(account.Value, credentials[1]))
            {
                var claims = new List<Claim>
                {
                    new Claim("name", credentials[0]),
                    new Claim(ClaimTypes.Role, "Admin"),
                };

                var identity = new ClaimsIdentity(claims, "Basic");
                var claimsPrincipal = new ClaimsPrincipal(identity);

                return AuthenticateResult.Success(new AuthenticationTicket(claimsPrincipal, Scheme.Name));
            }

            return AuthenticateResult.Fail("Invlid credentials.");
        }
        catch (Exception e)
        {
            return AuthenticateResult.Fail(e.Message);
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.Add("WWW-Authenticate", "Basic realm=\"semantico.com\"");
        return base.HandleChallengeAsync(properties);
    }
}