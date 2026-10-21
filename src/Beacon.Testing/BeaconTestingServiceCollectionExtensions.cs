using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Beacon.Core.Authentication;
using Beacon.Core.HostData;
using Beacon.Core.Mcp;

namespace Beacon.Testing;

/// <summary>Test-host wiring for a Beacon MCP gateway. Call these after <c>AddBeaconMcpGateway</c>, in a test only.</summary>
public static class BeaconTestingServiceCollectionExtensions
{
    /// <summary>
    /// Validates bearer tokens with <paramref name="tokens"/>' test key instead of the tenant's signing keys. Issuer,
    /// audience and lifetime are checked exactly as in production, and the caller mapping is the gateway's own.
    /// </summary>
    public static IServiceCollection TrustMcpTestTokens(this IServiceCollection services, McpTestTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var options = new JwtAuthenticationOptions { EnableBearerAuthentication = true };
        options.Validation.SigningKey = tokens.SigningKey;
        options.Validation.ValidIssuers = [tokens.Issuer];
        options.Validation.ValidAudiences = [$"api://{tokens.Audience}", tokens.Audience];
        options.Validate();
        services.Replace(ServiceDescriptor.Singleton(options));

        return services;
    }

    /// <summary>Makes <paramref name="users"/> the people the gateway knows (its <see cref="IMcpHostUserResolver"/>).</summary>
    public static IServiceCollection UseMcpTestUsers(this IServiceCollection services, McpTestUsers users)
    {
        ArgumentNullException.ThrowIfNull(users);

        services.RemoveAll<IMcpHostUserResolver>();
        services.AddSingleton<IMcpHostUserResolver>(users);

        return services;
    }

    /// <summary>
    /// Answers the host project as <paramref name="projectId"/> without syncing it, for a test without Beacon's database.
    /// Project access and the audit still need one; prefer a real database for tests that call tools.
    /// </summary>
    public static IServiceCollection UseMcpTestHostProject(this IServiceCollection services, int projectId)
    {
        services.Replace(ServiceDescriptor.Singleton<IBeaconHostProjectGate>(new FixedHostProject(projectId)));

        return services;
    }

    private sealed class FixedHostProject(int projectId) : IBeaconHostProjectGate
    {
        public BeaconHostSyncState State { get; } = new(true, 0, null, null);

        public Task<int?> GetProjectIdAsync(CancellationToken cancellationToken) => Task.FromResult<int?>(projectId);
    }
}
