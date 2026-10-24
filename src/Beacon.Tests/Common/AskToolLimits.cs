using Beacon.Core.Configuration;
using Beacon.MCP.Services;
using Microsoft.Extensions.Options;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// For tests that build the ask tool themselves: the service provider serves a limiter with the ask limit off, so the
/// tool's own limit (covered by RequestRateLimitTests) does not depend on how many questions other tests asked through
/// the stand-in limiter that serves a provider without a registration.
/// </summary>
internal static class AskToolLimits
{
    private static readonly McpRateLimiter Unlimited =
        new(Options.Create(new RateLimitOptions { Ask = new RateLimitWindow { PermitLimit = 0 } }));

    public static Mock<IServiceProvider> WithoutAskLimit(this Mock<IServiceProvider> serviceProvider)
    {
        serviceProvider.Setup(x => x.GetService(typeof(McpRateLimiter))).Returns(Unlimited);

        return serviceProvider;
    }
}
