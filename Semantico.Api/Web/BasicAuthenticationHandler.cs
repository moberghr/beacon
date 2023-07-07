using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Semantico.Api.Web
{
    public class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public BasicAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock)
            : base(options, logger, encoder, clock)
        {
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

                if (credentials[0] == "moberg" && credentials[1] == "3Semantico6#")
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
}
