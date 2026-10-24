using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Beacon.Api.Authentication;

namespace Beacon.Gateway;

/// <summary>
/// ASP.NET Core uses a host's only authentication scheme as its default when none is named. The gateway adds a second
/// scheme (<see cref="BeaconMcpGatewayDefaults.AuthenticationScheme"/>; Beacon's own schemes,
/// <see cref="BeaconAuthenticationDefaults.RegisteredSchemes"/>, are never counted as the host's), which would silently leave such a host without a
/// default — its own <c>[Authorize]</c> endpoints could no longer authenticate anybody. Startup refuses that, naming the fix.
/// </summary>
internal sealed class BeaconMcpGatewayStartupCheck(
    IAuthenticationSchemeProvider schemes,
    IOptions<AuthenticationOptions> authentication) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var options = authentication.Value;
        var authenticateDefault = options.DefaultAuthenticateScheme ?? options.DefaultScheme;
        var challengeDefault = options.DefaultChallengeScheme ?? options.DefaultScheme;
        var hostSchemes = schemes.GetAllSchemesAsync().GetAwaiter().GetResult()
            .Where(x => x.Name != BeaconMcpGatewayDefaults.AuthenticationScheme)
            .Where(x => !BeaconAuthenticationDefaults.RegisteredSchemes.Contains(x.Name))
            .ToList();

        // Authenticate and challenge each fall back to the only scheme on their own, so either one left unnamed is lost.
        if (hostSchemes.Count == 1 && (authenticateDefault == null || challengeDefault == null))
        {
            throw new InvalidOperationException(
                $"The application relies on '{hostSchemes[0].Name}' being its only authentication scheme, and the Beacon MCP gateway adds "
                + $"'{BeaconMcpGatewayDefaults.AuthenticationScheme}'. Name the default explicitly: services.AddAuthentication(\"{hostSchemes[0].Name}\").");
        }

        return next;
    }
}
