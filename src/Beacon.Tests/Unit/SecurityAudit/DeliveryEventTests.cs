using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataQuality.CreateDataContract;
using Beacon.Core.Handlers.DataQuality.UpdateDataContract;
using Beacon.Core.Handlers.Recipients;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Notifications;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.ChangeGovernance;
using Beacon.Tests.Unit.Notifications;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The delivery events: a recipient's create, change and delete (whether where it delivers or a secret changed, and the
/// change request it waits on — never its name, destination or headers), and a subscription's create, change, archive,
/// reactivation, recipients and SLA, each in the unit of work of its change, by the caller; and the attribution those
/// resources carry — set by every change, a submission for approval and each subscription step included, never by a
/// no-op — taken from the caller and never from a request (no request shape can carry it).
/// </summary>
[TestFixture]
public class DeliveryEventTests
{
    private const string Ada = "ext-ada";
    private const int AdaId = 3;
    private const string Secret = "T0KEN-PATH-9f8e";
    private const string Destination = "https://hooks.example.com/services/" + Secret;
    private const string Moved = "https://hooks.example.com/services/elsewhere-" + Secret;
    private static readonly DateTime Now = new(2026, 10, 14, 9, 30, 0, DateTimeKind.Utc);

    private RecordingSecurityAudit _audit = null!;
    private FakeTimeProvider _time = null!;

    [SetUp]
    public void SetUp()
    {
        _audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(AdaId, Ada));
        _time = new FakeTimeProvider(new DateTimeOffset(Now));
    }

    [Test]
    public async Task CreatingARecipient_RecordsRecipientCreated_AndItsCreator()
    {
        var store = new ChangeApprovalStore();

        var created = await CreateHandler(store, new BeaconConfiguration()).Handle(Create(Destination), CancellationToken.None);

        var recipientCreated = _audit.Single(Destination, Secret, "hooks.example.com", "ops hook");
        recipientCreated.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.RecipientCreated, ActorUserId = (int?)AdaId, ResourceType = SecurityAuditResources.Recipient, ResourceId = created.Id.ToString() },
            x => x.ExcludingMissingMembers());
        recipientCreated.DetailsJson.Should().Be("{\"notificationType\":\"Webhook\",\"changeApprovalId\":null}");
        _audit.Ways.Should().Equal("saved");
        store.Saved.OfType<Recipient>().Single().CreatedByUserId.Should().Be(Ada);
    }

    [Test]
    public async Task CreatingARecipient_UnderTheWorkflow_RecordsItsRequest_InTheSameUnitOfWork()
    {
        var store = new ChangeApprovalStore();

        await CreateHandler(store, TestChangeApprovals.On()).Handle(Create(Destination), CancellationToken.None);
        store.Commit();

        var request = store.Requests.Should().ContainSingle().Subject;
        _audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.RecipientCreated, SecurityAuditActions.ChangeSubmitted);
        _audit.Of(SecurityAuditActions.RecipientCreated).Single().DetailsJson.Should().Contain($"\"changeApprovalId\":{request.Id}");
        var submitted = _audit.Of(SecurityAuditActions.ChangeSubmitted).Single();
        submitted.ResourceType.Should().Be(SecurityAuditResources.ChangeApprovalRequest);
        submitted.ResourceId.Should().Be(request.Id.ToString());
        submitted.DetailsJson.Should().Be($"{{\"kind\":\"RecipientDestination\",\"resourceId\":{request.ResourceId},\"supersededRequestIds\":[]}}");
        foreach (var auditEvent in _audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent, Destination, Secret, "ops hook");
        }

        _audit.Ways.Should().Equal("saved", "saved");
        store.Offline.Commits.Should().Be(1, "the recipient, its request and both events commit together");
    }

    [Test]
    public async Task MovingARecipientsDestination_RecordsADestinationAndSecretChange_AndWhoChangedIt()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);

        await UpdateHandler(store).Handle(Update(recipient, Moved), CancellationToken.None);

        var updated = _audit.Single(Destination, Moved, Secret, "elsewhere");
        updated.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.RecipientUpdated, ActorUserId = (int?)AdaId, ResourceId = recipient.Id.ToString() },
            x => x.ExcludingMissingMembers());
        updated.DetailsJson.Should().Be(
            "{\"notificationType\":\"Webhook\",\"destinationChanged\":true,\"secretChanged\":true,\"nameChanged\":false," +
            "\"descriptionChanged\":false,\"bodyTemplateChanged\":false,\"changeApprovalId\":null}");
        recipient.ModifiedByUserId.Should().Be(Ada);
        recipient.ModifiedTime.Should().Be(Now);
    }

    [Test]
    public async Task ChangingOnlyAHeaderValue_IsASecretChange_NotADestinationChange()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination, headersJson: "{\"X-Api-Key\":\"old-value-123\"}");

        await UpdateHandler(store).Handle(Update(recipient, null, headersJson: "{\"X-Api-Key\":\"new-value-456\"}"), CancellationToken.None);

        var updated = _audit.Single("old-value-123", "new-value-456", Destination);
        updated.DetailsJson.Should().Contain("\"destinationChanged\":false,\"secretChanged\":true");
    }

    [Test]
    public async Task SavingARecipientUnchanged_RecordsNothing_AndAttributesNothing()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);

        await UpdateHandler(store).Handle(Update(recipient, null), CancellationToken.None);

        _audit.Events.Should().BeEmpty();
        recipient.ModifiedByUserId.Should().BeNull();
    }

    [Test]
    public async Task RenamingARecipient_RecordsTheNameChange_NotTheName()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);

        await UpdateHandler(store).Handle(Update(recipient, null, name: "night shift pager"), CancellationToken.None);

        _audit.Single("night shift pager", "ops hook").DetailsJson.Should().Contain("\"destinationChanged\":false,\"secretChanged\":false,\"nameChanged\":true");
    }

    [Test]
    public async Task DeletingARecipient_StagesRecipientDeleted_WithTheArchive()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);

        await DeleteHandler(store).Handle(new DeleteRecipientCommand(recipient.Id), CancellationToken.None);

        _audit.Single(Destination).Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.RecipientDeleted, ResourceId = recipient.Id.ToString(), DetailsJson = (string?)null },
            x => x.ExcludingMissingMembers());
        _audit.Ways.Should().Equal("staged");
        recipient.ArchivedTime.Should().NotBeNull();
        recipient.ModifiedByUserId.Should().Be(Ada);
        recipient.ModifiedTime.Should().Be(Now);
    }

    // Under the workflow the change is stored and submitted for approval in one unit of work: the caller is its last
    // modifier, and both events name them.
    [Test]
    public async Task ChangingARecipientsDestination_UnderTheWorkflow_AttributesTheChangeToTheCaller_AndRecordsItsSubmission()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);

        await WorkflowUpdateHandler(store).Handle(Update(recipient, Moved), CancellationToken.None);
        store.Commit();

        var request = store.Requests.Should().ContainSingle().Subject;
        recipient.ModifiedByUserId.Should().Be(Ada);
        recipient.ModifiedTime.Should().Be(Now);
        _audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.RecipientUpdated, SecurityAuditActions.ChangeSubmitted);
        _audit.Events.Should().OnlyContain(x => x.ActorUserId == AdaId);
        _audit.Of(SecurityAuditActions.RecipientUpdated).Single().DetailsJson.Should().Contain($"\"changeApprovalId\":{request.Id}");
        foreach (var auditEvent in _audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent, Destination, Moved, Secret, "ops hook");
        }
    }

    [Test]
    public async Task TheHostRecipientService_UnderTheWorkflow_AttributesTheChangeToTheCaller_AndRecordsItsSubmission()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);
        var service = new RecipientService(
            store.Factory(),
            new RecipientSecretEditor(NotificationTestKit.Policy(), store.Protector),
            TestChangeApprovals.Recipients(store.Protector, TestChangeApprovals.On(), TestChangeApprovals.Caller(Ada, AdaId)),
            RecordingSecurityAudit.Accessor(RecordingSecurityAudit.User(AdaId, Ada)),
            _audit,
            _time);

        (await service.UpdateRecipient(Data(recipient.Id, Moved), CancellationToken.None)).Success.Should().BeTrue();
        store.Commit();

        store.Requests.Should().ContainSingle();
        recipient.ModifiedByUserId.Should().Be(Ada);
        recipient.ModifiedTime.Should().Be(Now);
        _audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.RecipientUpdated, SecurityAuditActions.ChangeSubmitted);
        _audit.Events.Should().OnlyContain(x => x.ActorUserId == AdaId);
    }

    [Test]
    public async Task ARefusedDelete_RecordsNothing()
    {
        var store = new ChangeApprovalStore();
        var recipient = store.AddRecipient(Destination);
        recipient.Subscriptions.Add(new Subscription { Id = 70, QueryId = 3, CronExpression = "0 * * * *" });

        var act = () => DeleteHandler(store).Handle(new DeleteRecipientCommand(recipient.Id), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task TheHostRecipientService_RecordsCreateChangeAndDelete_AsTheHandlersDo()
    {
        var store = new ChangeApprovalStore();
        var service = new RecipientService(
            store.Factory(),
            new RecipientSecretEditor(NotificationTestKit.Policy(), store.Protector),
            TestChangeApprovals.Recipients(store.Protector),
            RecordingSecurityAudit.Accessor(RecordingSecurityAudit.User(AdaId, Ada)),
            _audit,
            _time);

        (await service.CreateRecipient(Data(null, Destination), CancellationToken.None)).Success.Should().BeTrue();
        store.Commit();
        var recipient = store.Recipients.Single();
        (await service.UpdateRecipient(Data(recipient.Id, Moved), CancellationToken.None)).Success.Should().BeTrue();
        await service.DeleteRecipient(recipient.Id, CancellationToken.None);

        _audit.Events.Select(x => x.Action).Should().Equal(
            SecurityAuditActions.RecipientCreated,
            SecurityAuditActions.RecipientUpdated,
            SecurityAuditActions.RecipientDeleted);
        foreach (var auditEvent in _audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent, Destination, Moved, Secret, "ops hook");
            auditEvent.ResourceId.Should().Be(recipient.Id.ToString());
        }

        recipient.CreatedByUserId.Should().Be(Ada);
        recipient.ModifiedByUserId.Should().Be(Ada);
    }

    [Test]
    public async Task CreatingASubscription_RecordsItsQueryRecipientsAndCreator()
    {
        var world = new SubscriptionWorld();
        world.Recipients.Add(new Recipient { Id = 41, Name = "ops", Destination = "x" });

        var data = NewSubscription(recipientIds: [41]);
        (await world.Service(_audit, _time).CreateSubscription(data, CancellationToken.None)).Success.Should().BeTrue();

        var subscription = world.Saved.OfType<Subscription>().Single();
        var created = _audit.Single();
        created.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.SubscriptionCreated, ActorUserId = (int?)AdaId, ResourceType = SecurityAuditResources.Subscription, ResourceId = subscription.Id.ToString() },
            x => x.ExcludingMissingMembers());
        created.DetailsJson.Should().Be("{\"queryId\":3,\"recipientIds\":[41],\"createTasks\":false}");
        _audit.Ways.Should().Equal("saved");
        subscription.CreatedByUserId.Should().Be(Ada);
        subscription.OwnerUserId.Should().Be(Ada);
    }

    [Test]
    public async Task ChangingASubscription_RecordsTheRecipientsAddedAndRemoved_AndWhetherItsScheduleChanged()
    {
        var world = new SubscriptionWorld();
        var kept = new Recipient { Id = 41, Name = "ops", Destination = "x" };
        var dropped = new Recipient { Id = 42, Name = "old", Destination = "x" };
        var added = new Recipient { Id = 43, Name = "new", Destination = "x" };
        world.Recipients.AddRange([kept, dropped, added]);
        var subscription = world.AddSubscription(recipients: [kept, dropped]);

        var data = NewSubscription(recipientIds: [41, 43]);
        data.SubscriptionId = subscription.Id;
        data.CronExpression = "0 9 * * *";
        await world.Service(_audit, _time).UpdateSubscription(data, CancellationToken.None);

        var updated = _audit.Single();
        updated.Action.Should().Be(SecurityAuditActions.SubscriptionUpdated);
        updated.DetailsJson.Should().Be("{\"scheduleChanged\":true,\"addedRecipientIds\":[43],\"removedRecipientIds\":[42]}");
        _audit.Ways.Should().Equal("staged");
        subscription.ModifiedByUserId.Should().Be(Ada);
        subscription.ModifiedTime.Should().Be(Now);
    }

    [Test]
    public async Task ArchivingAndReactivatingASubscription_RecordsEach()
    {
        var world = new SubscriptionWorld();
        var subscription = world.AddSubscription();
        var service = world.Service(_audit, _time);

        await service.DeleteSubscription(subscription.Id, CancellationToken.None);
        await service.ReactivateSubscription(subscription.Id, CancellationToken.None);

        _audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.SubscriptionArchived, SecurityAuditActions.SubscriptionReactivated);
        _audit.Events.Should().OnlyContain(x => x.ResourceId == subscription.Id.ToString() && x.ActorUserId == AdaId);
        _audit.Ways.Should().Equal("staged", "staged");
        subscription.ModifiedByUserId.Should().Be(Ada);
    }

    // Each step on its own: the attribution is cleared between them, and the clock moves.
    [Test]
    public async Task ArchivingAndReactivatingASubscription_EachAttributesTheCaller_AtItsOwnTime()
    {
        var world = new SubscriptionWorld();
        var subscription = world.AddSubscription();
        var service = world.Service(_audit, _time);

        await service.DeleteSubscription(subscription.Id, CancellationToken.None);

        subscription.ModifiedByUserId.Should().Be(Ada);
        subscription.ModifiedTime.Should().Be(Now);

        subscription.ModifiedByUserId = null;
        _time.Advance(TimeSpan.FromHours(1));
        await service.ReactivateSubscription(subscription.Id, CancellationToken.None);

        subscription.ModifiedByUserId.Should().Be(Ada, "reactivating changes the subscription");
        subscription.ModifiedTime.Should().Be(Now.AddHours(1));
    }

    [Test]
    public async Task AttachingAndDetachingRecipients_EachAttributesTheCaller_AndANoOpAttributesNothing()
    {
        var world = new SubscriptionWorld();
        var attached = new Recipient { Id = 41, Name = "ops", Destination = "x" };
        world.Recipients.AddRange([attached, new Recipient { Id = 42, Name = "new", Destination = "x" }]);
        var subscription = world.AddSubscription(recipients: [attached]);
        var service = world.Service(_audit, _time);

        await service.AddRecipients(subscription.Id, [41], CancellationToken.None);
        await service.RemoveRecipient(subscription.Id, 99, CancellationToken.None);

        subscription.ModifiedByUserId.Should().BeNull("nothing was attached or removed");
        subscription.ModifiedTime.Should().BeNull();

        await service.AddRecipients(subscription.Id, [42], CancellationToken.None);

        subscription.ModifiedByUserId.Should().Be(Ada);
        subscription.ModifiedTime.Should().Be(Now);

        subscription.ModifiedByUserId = null;
        _time.Advance(TimeSpan.FromHours(1));
        await service.RemoveRecipient(subscription.Id, 41, CancellationToken.None);

        subscription.ModifiedByUserId.Should().Be(Ada, "detaching a recipient changes the subscription");
        subscription.ModifiedTime.Should().Be(Now.AddHours(1));
    }

    [Test]
    public async Task AttachingAndDetachingRecipients_RecordsEach_AndANoOpRecordsNothing()
    {
        var world = new SubscriptionWorld();
        var attached = new Recipient { Id = 41, Name = "ops", Destination = "x" };
        world.Recipients.AddRange([attached, new Recipient { Id = 42, Name = "new", Destination = "x" }]);
        var subscription = world.AddSubscription(recipients: [attached]);
        var service = world.Service(_audit, _time);

        await service.AddRecipients(subscription.Id, [41], CancellationToken.None);
        await service.RemoveRecipient(subscription.Id, 99, CancellationToken.None);
        _audit.Events.Should().BeEmpty("nothing new was attached and nothing attached was removed");

        await service.AddRecipients(subscription.Id, [41, 42], CancellationToken.None);
        await service.RemoveRecipient(subscription.Id, 41, CancellationToken.None);

        _audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.SubscriptionRecipientsAdded, SecurityAuditActions.SubscriptionRecipientRemoved);
        _audit.Events[0].DetailsJson.Should().Be("{\"recipientIds\":[42]}");
        _audit.Events[1].DetailsJson.Should().Be("{\"recipientId\":41}");
    }

    [Test]
    public async Task ChangingASubscriptionsSla_RecordsBeforeAndAfter_AndAnUnchangedSlaRecordsNothing()
    {
        var world = new SubscriptionWorld();
        var subscription = world.AddSubscription();
        subscription.SlaHours = 24;
        var handler = new SetSubscriptionSlaHandler(world.Factory(), RecordingSecurityAudit.Accessor(RecordingSecurityAudit.User(AdaId, Ada)), _audit, _time);

        await handler.Handle(new SetSubscriptionSlaCommand(subscription.Id, 24), CancellationToken.None);
        _audit.Events.Should().BeEmpty();

        await handler.Handle(new SetSubscriptionSlaCommand(subscription.Id, 8), CancellationToken.None);

        var changed = _audit.Single();
        changed.Action.Should().Be(SecurityAuditActions.SubscriptionSlaChanged);
        changed.DetailsJson.Should().Be("{\"slaHoursBefore\":24,\"slaHoursAfter\":8}");
        subscription.ModifiedByUserId.Should().Be(Ada);
    }

    // Attribution is always the caller's: no command, request or DTO a client or a host sends can carry who created or
    // changed a resource, or when. Scanned, not listed: every MediatR request, every body an endpoint binds, the inputs of
    // the services a host calls, and every Beacon type reachable from their properties.
    [Test]
    public void NoRequestShape_CarriesAttribution()
    {
        string[] attribution = [nameof(IAttributedEntity.CreatedByUserId), nameof(IAttributedEntity.ModifiedByUserId), nameof(IAttributedEntity.ModifiedTime)];

        var shapes = RequestShapes();

        shapes.Should().Contain(
            [
                typeof(CreateRecipientCommand), typeof(UpdateRecipientCommand), typeof(RecipientData), typeof(CreateSubscriptionCommand),
                typeof(SubscriptionData), typeof(CreateDataContractCommand), typeof(UpdateDataContractCommand), typeof(CreateMigrationJobRequest),
                typeof(QueryData)
            ],
            "the scan reaches the requests, the bodies and the shapes nested in them");
        foreach (var shape in shapes)
        {
            shape.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(x => x.Name)
                .Should().NotContain(attribution, $"{shape.FullName} is what a client or a host sends");
        }
    }

    private static List<Type> RequestShapes()
    {
        var bodies = typeof(Beacon.Api.Endpoints.BeaconApiEndpoints).Assembly
            .GetTypes()
            .Where(x => x.Namespace == typeof(Beacon.Api.Endpoints.BeaconApiEndpoints).Namespace)
            .Where(x => x.Name.EndsWith("Body", StringComparison.Ordinal) || x.Name.EndsWith("Request", StringComparison.Ordinal));

        // What a host hands the services it calls directly (the host recipient, subscription, query and migration services).
        Type[] hostInputs = [typeof(RecipientData), typeof(SubscriptionData), typeof(QueryData), typeof(CreateMigrationJobRequest)];

        var found = new HashSet<Type>();
        var pending = new Queue<Type>(BeaconApiRoutes.RequestTypes().Concat(bodies).Concat(hostInputs));
        while (pending.Count > 0)
        {
            var shape = pending.Dequeue();
            if (!found.Add(shape))
            {
                continue;
            }

            foreach (var property in shape.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var nested in BeaconTypesIn(property.PropertyType))
                {
                    pending.Enqueue(nested);
                }
            }
        }

        return found.ToList();
    }

    // The Beacon classes and records a property type is or holds, however deeply (element and generic argument types).
    private static IEnumerable<Type> BeaconTypesIn(Type type)
    {
        if (type.IsClass && type.Namespace?.StartsWith("Beacon.", StringComparison.Ordinal) == true)
        {
            yield return type;
        }

        Type[] held = type.IsArray ? [type.GetElementType()!] : type.IsGenericType ? type.GetGenericArguments() : [];
        foreach (var part in held)
        {
            foreach (var nested in BeaconTypesIn(part))
            {
                yield return nested;
            }
        }
    }

    private static CreateRecipientCommand Create(string destination) =>
        new("ops hook", null, destination, (int)NotificationType.Webhook, null, null);

    private static UpdateRecipientCommand Update(Recipient recipient, string? destination, string? name = null, string? headersJson = null) =>
        new(recipient.Id, name ?? recipient.Name, null, destination, (int)NotificationType.Webhook, headersJson, null);

    private static RecipientData Data(int? id, string destination) =>
        new() { RecipientId = id, Name = "ops hook", Destination = destination, NotificationType = NotificationType.Webhook };

    private static SubscriptionData NewSubscription(List<int> recipientIds) =>
        new()
        {
            QueryId = 3,
            CronExpression = "0 8 * * *",
            Recipients = recipientIds
                .Select(x => new RecipientData { RecipientId = x, Name = string.Empty, Destination = string.Empty })
                .ToList()
        };

    private CreateRecipientHandler CreateHandler(ChangeApprovalStore store, BeaconConfiguration configuration) =>
        new(
            store.Factory(),
            new RecipientSecretEditor(NotificationTestKit.Policy(), store.Protector),
            TestChangeApprovals.Recipients(store.Protector, configuration, TestChangeApprovals.Caller(Ada, AdaId)),
            RecordingSecurityAudit.Accessor(RecordingSecurityAudit.User(AdaId, Ada)),
            _audit);

    private UpdateRecipientHandler UpdateHandler(ChangeApprovalStore store) =>
        new(
            store.Factory(),
            new RecipientSecretEditor(NotificationTestKit.Policy(), store.Protector),
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
            RecordingSecurityAudit.Accessor(RecordingSecurityAudit.User(AdaId, Ada)),
            TestPermissions.Admin,
            TestChangeApprovals.Recipients(store.Protector),
            _audit,
            _time,
            NullLogger<UpdateRecipientHandler>.Instance);

    private UpdateRecipientHandler WorkflowUpdateHandler(ChangeApprovalStore store) =>
        new(
            store.Factory(),
            new RecipientSecretEditor(NotificationTestKit.Policy(), store.Protector),
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
            RecordingSecurityAudit.Accessor(RecordingSecurityAudit.User(AdaId, Ada)),
            TestPermissions.Admin,
            TestChangeApprovals.Recipients(store.Protector, TestChangeApprovals.On(), TestChangeApprovals.Caller(Ada, AdaId)),
            _audit,
            _time,
            NullLogger<UpdateRecipientHandler>.Instance);

    private DeleteRecipientHandler DeleteHandler(ChangeApprovalStore store) =>
        new(store.Factory(), RecordingSecurityAudit.Accessor(RecordingSecurityAudit.User(AdaId, Ada)), _audit, _time);

    // Subscriptions, their query and recipients behind a recording context (§4.7: no database): saves give new rows ids.
    private sealed class SubscriptionWorld
    {
        private int _nextId = 500;

        public List<Subscription> Subscriptions { get; } = [];

        public List<Query> Queries { get; } = [new Query { Id = 3, Name = "Overdue invoices" }];

        public List<Recipient> Recipients { get; } = [];

        public List<object> Saved { get; } = [];

        public Subscription AddSubscription(List<Recipient>? recipients = null)
        {
            var subscription = new Subscription
            {
                Id = _nextId++,
                QueryId = 3,
                Query = Queries[0],
                CronExpression = "0 8 * * *",
                Recipients = recipients ?? [],
                Parameters = []
            };
            Subscriptions.Add(subscription);

            return subscription;
        }

        public IDbContextFactory<BeaconContext> Factory()
        {
            var sets = new Dictionary<Type, object>
            {
                [typeof(Subscription)] = RecordingBeaconContext.MemorySet(Subscriptions, Saved),
                [typeof(Query)] = RecordingBeaconContext.MemorySet(Queries, Saved),
                [typeof(QueryStep)] = RecordingBeaconContext.MemorySet(new List<QueryStep>(), Saved),
                [typeof(QueryParameter)] = RecordingBeaconContext.MemorySet(new List<QueryParameter>(), Saved),
                [typeof(SubscriptionParameter)] = RecordingBeaconContext.MemorySet(new List<SubscriptionParameter>(), Saved),
                [typeof(Recipient)] = RecordingBeaconContext.MemorySet(Recipients, Saved)
            };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(sets, Saved, onSave: GiveIds));

            return factory.Object;
        }

        public SubscriptionService Service(ISecurityAuditRecorder audit, TimeProvider time) =>
            new(
                Factory(),
                Mock.Of<IBeaconScheduler>(),
                Mock.Of<IAnomalyDetectionService>(),
                RecordingSecurityAudit.Accessor(RecordingSecurityAudit.User(AdaId, Ada)),
                new BeaconConfiguration(),
                Options.Create(new QueryExecutionOptions()),
                audit,
                time);

        private void GiveIds()
        {
            foreach (var entity in Saved.OfType<BaseEntity>().Where(x => x.Id == 0))
            {
                entity.Id = _nextId++;
            }
        }
    }
}
