using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Beacon.Gateway;

/// <summary>
/// ASP.NET Core uses a host's only authentication scheme as its default when none is named. The gateway adds a second
/// scheme (<see cref="BeaconMcpGatewayDefaults.AuthenticationScheme"/>), which would silently leave such a host without a
/// default — its own <c>[Authorize]</c> endpoints could no longer authenticate anybody. Startup refuses that, naming the fix.
/// </summary>
internal sealed class BeaconMcpGatewayStartupCheck(
    IAuthenticationSchemeProvider schemes,
    IOptions<AuthenticationOptions> authentication) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var options = authentication.Value;
        var named = options.DefaultScheme ?? options.DefaultAuthenticateScheme ?? options.DefaultChallengeScheme;
        var hostSchemes = schemes.GetAllSchemesAsync().GetAwaiter().GetResult()
            .Where(x => x.Name != BeaconMcpGatewayDefaults.AuthenticationScheme)
            .ToList();

        if (named == null && hostSchemes.Count == 1)
        {
            throw new InvalidOperationException(
                $"The application relies on '{hostSchemes[0].Name}' being its only authentication scheme, and the Beacon MCP gateway adds "
                + $"'{BeaconMcpGatewayDefaults.AuthenticationScheme}'. Name the default explicitly: services.AddAuthentication(\"{hostSchemes[0].Name}\").");
        }

        return next;
    }
}
