using Beacon.Core.Authentication;

namespace Beacon.Api.Authentication;

/// <summary>
/// Refuses to start outside Development while a JWT endpoint (<see cref="JwtAuthenticationOptions.ExternalLoginEndpoint"/>,
/// <see cref="JwtValidationOptions.JwksEndpoint"/>) uses <c>http</c>. <see cref="JwtAuthenticationOptions.Validate"/>
/// already refuses <c>http</c> to any host but a loopback one; this check limits even that to Development, where a local
/// identity provider may run without TLS. The message names the setting, never the URL.
/// </summary>
internal sealed class JwtEndpointStartupCheck(
    JwtAuthenticationOptions options,
    IHostEnvironment? environment = null) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        Check();

        return next;
    }

    internal void Check()
    {
        if (environment?.IsDevelopment() == true)
        {
            return;
        }

        var insecure = options.LoopbackHttpEndpoints();
        if (insecure.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"JWT {string.Join(" and ", insecure)} must be an https URL outside Development: http is accepted only for a " +
            "loopback host in Development.");
    }
}
