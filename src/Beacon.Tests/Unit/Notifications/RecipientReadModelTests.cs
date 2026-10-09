using System.Text.Json;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataQuality.GetDataContractDetail;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Models;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Services;
using Beacon.Core.Worker;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// Subscription and data-contract reads never carry a recipient's destination: the SQL they run does not even select
/// the column, the mapped results leave it empty, and archived recipients are not projected under
/// <c>IgnoreQueryFilters</c>.
/// </summary>
[TestFixture]
public class RecipientReadModelTests
{
    [Test]
    public async Task SubscriptionDetails_NeverSelectsDestinationsOrArchivedRecipients()
    {
        var database = new CommandCapture();
        var service = new SubscriptionService(database.NpgsqlFactory(), Mock.Of<IBeaconScheduler>(), Mock.Of<IAnomalyDetectionService>());

        var act = () => service.GetSubscriptionDetails(5, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("the fake database returns no row");
        var sql = database.Commands.Should().ContainSingle().Subject.Text;
        NotSelectingSecretColumns(sql);
        sql.Should().MatchRegex(@"r\d*\.archived_time IS NULL", "IgnoreQueryFilters drops the recipients' filter, so it is re-applied");
    }

    [Test]
    public async Task SubscriptionList_NeverSelectsDestinations()
    {
        var database = new CommandCapture();
        var service = new SubscriptionService(database.NpgsqlFactory(), Mock.Of<IBeaconScheduler>(), Mock.Of<IAnomalyDetectionService>());

        await service.GetSubscriptions(null, null, null, null!, CancellationToken.None);

        NotSelectingSecretColumns(database.Commands.Should().ContainSingle().Subject.Text);
    }

    [Test]
    public async Task DataContractDetail_NeverSelectsDestinations()
    {
        var database = new CommandCapture();
        var handler = new GetDataContractDetailHandler(database.NpgsqlFactory());

        var act = () => handler.Handle(new GetDataContractDetailQuery(3), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconException>("the fake database returns no row");
        NotSelectingSecretColumns(database.Commands.Should().ContainSingle().Subject.Text);
    }

    // The recipients' destination and headers columns (snake_case on PostgreSQL); a constant projected under the
    // "Destination" alias is not one of them.
    private static void NotSelectingSecretColumns(string sql)
    {
        sql.Should().NotMatchRegex(@"\.destination\b").And.NotContain("headers_json");
    }

    [Test]
    public async Task SubscriptionDetailHandler_NeverReturnsADestination()
    {
        var service = new Mock<ISubscriptionService>();
        service
            .Setup(x => x.GetSubscriptionDetails(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionDetailsData
            {
                SubscriptionId = 5,
                QueryName = "daily",
                Recipients = [new RecipientData { RecipientId = 9, Name = "hook", Destination = "https://hooks.example.com/in?token=PATHSECRET", NotificationType = NotificationType.Webhook }],
                Parameters = [],
            });

        var result = await new GetSubscriptionDetailHandler(service.Object).Handle(new GetSubscriptionDetailQuery(5), CancellationToken.None);

        result.Detail.Recipients.Should().ContainSingle().Which.Destination.Should().BeNull();
        JsonSerializer.Serialize(result).Should().NotContain("PATHSECRET");
    }
}
