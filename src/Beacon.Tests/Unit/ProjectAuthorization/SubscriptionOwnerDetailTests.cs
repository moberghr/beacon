using FluentAssertions;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// A subscription's detail names its owner, whose access its scheduled runs are checked against: whether it has one, and
/// the owner's display name (or a neutral label, never a user name or external id), read in the same command as the rest
/// of the detail.
/// </summary>
[TestFixture]
public class SubscriptionOwnerDetailTests
{
    [Test]
    public async Task SubscriptionDetailsWithOwner_Translates()
    {
        var database = new CommandCapture();
        var service = new SubscriptionService(database.NpgsqlFactory(), Mock.Of<IBeaconScheduler>(), Mock.Of<IAnomalyDetectionService>(), TestProjectAccess.Actors(), new BeaconConfiguration());

        var act = () => service.GetSubscriptionDetails(5, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("the fake database returns no row");
        var sql = database.Commands.Should().ContainSingle().Subject.Text;
        sql.Should().Contain("owner_user_id");
        sql.Should().MatchRegex(@"COALESCE\(u\d*\.display_name, 'User ' \|\| u\d*\.id::text\)", "the owner is named by display name or a neutral label, never the user name");
        sql.Should().NotContain("user_name");
        sql.Should().MatchRegex(@"u\d*\.external_id = s\.owner_user_id");
    }

    [Test]
    public async Task SubscriptionDetailHandler_ReturnsTheOwner()
    {
        var service = new Mock<ISubscriptionService>();
        service
            .Setup(x => x.GetSubscriptionDetails(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionDetailsData
            {
                SubscriptionId = 5,
                QueryName = "daily",
                Status = "Active",
                OwnerUserId = "ext-ada",
                OwnerDisplayName = "Ada Lovelace"
            });

        var result = await new GetSubscriptionDetailHandler(service.Object).Handle(new GetSubscriptionDetailQuery(5), CancellationToken.None);

        result.Detail.HasOwner.Should().BeTrue();
        result.Detail.OwnerDisplayName.Should().Be("Ada Lovelace");
    }

    [Test]
    public async Task SubscriptionDetailHandler_WithoutAnOwner_ReturnsNone()
    {
        var service = new Mock<ISubscriptionService>();
        service
            .Setup(x => x.GetSubscriptionDetails(6, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionDetailsData { SubscriptionId = 6, QueryName = "legacy", Status = "Active" });

        var result = await new GetSubscriptionDetailHandler(service.Object).Handle(new GetSubscriptionDetailQuery(6), CancellationToken.None);

        result.Detail.HasOwner.Should().BeFalse();
        result.Detail.OwnerDisplayName.Should().BeNull();
    }
}
