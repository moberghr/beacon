using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Semantico.Core.Services;

namespace Semantico.Api.Web;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IAccountService _accountService;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAccountService accountService): base (options, logger, encoder)
    {
        _accountService = accountService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            Request.Headers.TryGetValue(SemanticoAuth.ApiKeyHeaderName, out var authHeader);
            var authValue = authHeader.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(authValue))
            {
                return AuthenticateResult.Fail("Missing API key.");
            }

            var account = await _accountService.GetAccountByApiKeyAsync(authValue, default);

            if (account == null)
            {
                return AuthenticateResult.Fail("Invalid API key.");
            }

            var claims = new List<Claim>
                {
                    new Claim(SemanticoAuth.AccountId, account.AccountId.ToString()),
                    new Claim(SemanticoAuth.ApiKey, account.ApiKey!),
                    new Claim(ClaimTypes.Role, "Admin")
                };

            var identity = new ClaimsIdentity(claims, SemanticoAuth.ApiKeyHeaderName);
            var claimsPrincipal = new ClaimsPrincipal(identity);

            return AuthenticateResult.Success(new AuthenticationTicket(claimsPrincipal, Scheme.Name));
        }
        catch (Exception e)
        {
            return AuthenticateResult.Fail(e.Message);
        }
    }
}