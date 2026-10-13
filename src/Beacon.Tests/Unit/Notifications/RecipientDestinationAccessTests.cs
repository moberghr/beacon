using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Recipients;
using Beacon.Core.Models;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// A recipient's destination decides where every subscription and data contract it is attached to sends its results.
/// A SecurityAdmin changes where a recipient delivers only when it can read everything the recipient is attached to (403
/// otherwise); it may still rename a recipient attached to work it cannot read. An Admin is not checked.
/// </summary>
[TestFixture]
public class RecipientDestinationAccessTests
{
    private const int ReadableSource = 10;
    private const int UnreadableSource = 20;
    private const string OldDestination = "ops@example.com";
    private const string NewDestination = "elsewhere@example.org";

    private List<Recipient> _recipients = null!;
    private List<Subscription> _subscriptions = null!;
    private List<DataContract> _contracts = null!;
    private List<object> _saved = null!;

    [SetUp]
    public void SetUp()
    {
        var protector = NotificationTestKit.Protector();
        _recipients =
        [
            new Recipient { Id = 1, Name = "on readable work", NotificationType = NotificationType.Email, Destination = protector.Protect(OldDestination) },
            new Recipient { Id = 2, Name = "on another project's subscription", NotificationType = NotificationType.Email, Destination = protector.Protect(OldDestination) },
            new Recipient { Id = 3, Name = "on another project's contract", NotificationType = NotificationType.Email, Destination = protector.Protect(OldDestination) },
            new Recipient { Id = 4, Name = "attached to nothing", NotificationType = NotificationType.Email, Destination = protector.Protect(OldDestination) }
        ];
        _subscriptions =
        [
            Subscription(100, ReadableSource, _recipients[0]),
            Subscription(200, UnreadableSource, _recipients[1])
        ];
        _contracts =
        [
            Contract(300, ReadableSource, _recipients[0]),
            Contract(400, UnreadableSource, _recipients[2])
        ];
        _saved = [];
    }

    [Test]
    public async Task ASecurityAdmin_RedirectsARecipient_WhoseWorkItReads()
    {
        await UpdateAsync(SecurityAdmin(), 1, NewDestination);
        await UpdateAsync(SecurityAdmin(), 4, NewDestination);

        NotificationTestKit.Protector().Unprotect(_recipients[0].Destination).Should().Be(NewDestination);
        NotificationTestKit.Protector().Unprotect(_recipients[3].Destination).Should().Be(NewDestination);
    }

    [TestCase(2)]
    [TestCase(3)]
    public async Task ASecurityAdmin_RedirectingARecipientOfWorkItCannotRead_IsForbidden(int recipientId)
    {
        var act = () => UpdateAsync(SecurityAdmin(), recipientId, NewDestination);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        NotificationTestKit.Protector().Unprotect(_recipients[recipientId - 1].Destination).Should().Be(OldDestination);
    }

    [Test]
    public async Task ASecurityAdmin_RenamesARecipientOfWorkItCannotRead_KeepingItsDestination()
    {
        await UpdateAsync(SecurityAdmin(), 2, destination: null, name: "renamed");

        _recipients[1].Name.Should().Be("renamed");
        NotificationTestKit.Protector().Unprotect(_recipients[1].Destination).Should().Be(OldDestination);
    }

    [Test]
    public async Task AnAdmin_RedirectsAnyRecipient()
    {
        await UpdateAsync(TestPermissions.Admin, 2, NewDestination);

        NotificationTestKit.Protector().Unprotect(_recipients[1].Destination).Should().Be(NewDestination);
    }

    private Task UpdateAsync(IBeaconPermissionAccessor permissions, int recipientId, string? destination, string? name = null)
    {
        // The caller reads project 1, which links the readable data source only.
        var scope = TestProjectAccess.Scope(new Dictionary<int, ProjectRole> { [1] = ProjectRole.Editor }, includesOpen: false, notOpen: []);
        var handler = new UpdateRecipientHandler(
            Factory(),
            NotificationTestKit.Editor(),
            TestProjectAccess.Evaluator(scope),
            TestProjectAccess.Actors(),
            permissions,
            TestChangeApprovals.Recipients(NotificationTestKit.Protector()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateRecipientHandler>.Instance);
        var recipient = _recipients.First(x => x.Id == recipientId);

        return handler.Handle(
            new UpdateRecipientCommand(recipientId, name ?? recipient.Name, null, destination, (int)NotificationType.Email, null, null),
            CancellationToken.None);
    }

    private static IBeaconPermissionAccessor SecurityAdmin() => TestPermissions.Holding(BeaconPermissions.SecurityAdmin);

    private IDbContextFactory<BeaconContext> Factory()
    {
        var links = new List<ProjectDataSource> { new() { ProjectId = 1, DataSourceId = ReadableSource } };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(Recipient)] = RecordingBeaconContext.MemorySet(_recipients, _saved),
                    [typeof(Subscription)] = RecordingBeaconContext.MemorySet(_subscriptions, _saved),
                    [typeof(DataContract)] = RecordingBeaconContext.MemorySet(_contracts, _saved),
                    [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, _saved)
                },
                _saved));

        return factory.Object;
    }

    private static Subscription Subscription(int id, int dataSourceId, Recipient recipient)
    {
        return new Subscription
        {
            Id = id,
            QueryId = id,
            CronExpression = "0 * * * *",
            Query = new Query
            {
                Id = id,
                Name = $"query {id}",
                Steps = [new QueryStep { QueryId = id, DataSourceId = dataSourceId, StepOrder = 1, SqlValue = "SELECT 1" }]
            },
            Recipients = [recipient]
        };
    }

    private static DataContract Contract(int id, int dataSourceId, Recipient recipient)
    {
        return new DataContract
        {
            Id = id,
            DataSourceId = dataSourceId,
            SchemaName = "public",
            TableName = "orders",
            Name = $"contract {id}",
            CronExpression = "0 * * * *",
            Recipients = [recipient]
        };
    }
}
