using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>The row-data egress log as Beacon registers it, for a caller whose log id is <see cref="CallerId"/>.</summary>
internal static class TestRowDataEgress
{
    public const string CallerId = "42";

    /// <summary>Logs nowhere; requests go through the provider handed in.</summary>
    public static ILlmRowDataEgress Discarding()
    {
        return Logging(NullLogger<LlmRowDataEgress>.Instance);
    }

    public static ILlmRowDataEgress Logging(ILogger<LlmRowDataEgress> logger)
    {
        var actors = new Mock<IBeaconActorAccessor>();
        actors
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconActor(CallerId, false));

        return new LlmRowDataEgress(actors.Object, logger);
    }
}
