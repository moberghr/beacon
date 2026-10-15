using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Common;

/// <summary>
/// What an <c>McpAuditService</c> needs beside its long-standing dependencies in tests: a content hasher under a throwaway
/// key, and (for containers built without <c>AddBeaconServices</c>) the scoped statement log.
/// </summary>
internal static class TestMcpAudit
{
    public static McpContentHasher ContentHasher { get; } = new(SecurityAuditTestKit.TestSecret);

    /// <summary>Registers the content hasher and the scoped statement log, as <c>AddBeaconServices</c> does.</summary>
    public static IServiceCollection AddMcpAuditEvidence(this IServiceCollection services)
    {
        services.TryAddSingleton(ContentHasher);
        services.TryAddScoped<IMcpStatementLog, McpStatementLog>();

        return services;
    }
}
