using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Semantico.Api.Services;
using Semantico.Api.Types;

namespace Semantico.Api.Web;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IAccountService _accountService;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISystemClock clock,
        IAccountService accountService)
        : base (
            options,
            logger,
            encoder,
            clock
    )
    {
        _accountService = accountService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            Request.Headers.TryGetValue(Constants.SemanticoApiKeyHeaderName, out var authHeader);
            var authValue = authHeader.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(authValue))
            {
                return AuthenticateResult.Fail("Missing API key.");
            }

            var account = await _accountService.GetAccountByApiKeyAsync(authValue);

            if (account == null)
            {
                return AuthenticateResult.Fail("Invalid API key.");
            }

            var claims = new List<Claim>
                {
                    new Claim(AccountClaimType.AccountId, account.Id.ToString()),
                    new Claim(AccountClaimType.ApiKey, account.Value),
                    new Claim(ClaimTypes.Role, "Admin")
                };

            var identity = new ClaimsIdentity(claims, Constants.SemanticoApiKeyHeaderName);
            var claimsPrincipal = new ClaimsPrincipal(identity);

            return AuthenticateResult.Success(new AuthenticationTicket(claimsPrincipal, Scheme.Name));
        }
        catch (Exception e)
        {
            return AuthenticateResult.Fail(e.Message);
        }
    }
}