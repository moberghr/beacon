using Beacon.AI.Handlers.Projects;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Worker;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The documentation job's status events go to the hub id of the caller who enqueued it: the actor's
/// <c>UserId</c> (<c>Users.ExternalId</c>), the id the hub addresses a session by, never a claim a host's claims
/// transformation may set to something else.
/// </summary>
[TestFixture]
public class GenerateProjectDocumentationHandlerTests
{
    [Test]
    public async Task TheJob_NotifiesTheCallerByTheirActorUserId()
    {
        var scheduler = new Mock<IBeaconScheduler>();
        scheduler
            .Setup(x => x.EnqueueProjectDocumentation(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync("job-1");
        var handler = Handler(scheduler, new BeaconActor("ext-ana", false));

        var result = await handler.Handle(new GenerateProjectDocumentationCommand(7, 0), CancellationToken.None);

        result.JobId.Should().Be("job-1");
        scheduler.Verify(x => x.EnqueueProjectDocumentation(7, 12, "ext-ana"), Times.Once);
    }

    [Test]
    public async Task ACallerWithoutAUser_IsNotNotified()
    {
        var scheduler = new Mock<IBeaconScheduler>();
        scheduler
            .Setup(x => x.EnqueueProjectDocumentation(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync("job-2");
        var handler = Handler(scheduler, BeaconActor.Nobody);

        await handler.Handle(new GenerateProjectDocumentationCommand(7, 0), CancellationToken.None);

        scheduler.Verify(x => x.EnqueueProjectDocumentation(7, 12, null), Times.Once);
    }

    private static GenerateProjectDocumentationHandler Handler(Mock<IBeaconScheduler> scheduler, BeaconActor actor)
    {
        var actors = new Mock<IBeaconActorAccessor>();
        actors.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(actor);
        var resolver = new Mock<IActorUserResolver>();
        resolver.Setup(x => x.ResolveActorUserIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(12);

        return new GenerateProjectDocumentationHandler(scheduler.Object, actors.Object, resolver.Object);
    }
}
