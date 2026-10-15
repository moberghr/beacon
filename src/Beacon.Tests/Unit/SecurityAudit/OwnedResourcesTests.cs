using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Handlers.Users;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// A user's owned and attributed resources, one kind per page, for offboarding: the user is found by <c>Users.Id</c>
/// (archived users too), each kind is matched by the external id its owner, creator and last-modifier columns record, as
/// parameters, on both providers; recipients and migration jobs have no owner. Only live resources are listed, each saying
/// whether the user owns it, created it or changed it last. An unknown user is 404. The read is recorded first, and fails
/// when it cannot be; an Auditor reads every name, a UserAdmin only those of the resources its projects reach (the others
/// are listed by id and kind).
/// </summary>
[TestFixture]
public class OwnedResourcesTests
{
    [TestCase(OwnedResourceKind.Query, false, "FROM beacon.queries", "q.owner_user_id = @", "q.created_by_user_id = @", "q.modified_by_user_id = @")]
    [TestCase(OwnedResourceKind.Subscription, false, "FROM beacon.subscriptions", "s.owner_user_id = @", "s.created_by_user_id = @", "s.modified_by_user_id = @")]
    [TestCase(OwnedResourceKind.DataContract, false, "FROM beacon.data_contracts", "d.owner_user_id = @", "d.created_by_user_id = @", "d.modified_by_user_id = @")]
    [TestCase(OwnedResourceKind.Recipient, false, "FROM beacon.recipients", "r.created_by_user_id = @", "r.modified_by_user_id = @")]
    [TestCase(OwnedResourceKind.MigrationJob, false, "FROM beacon.migration_jobs", "m.created_by_user_id = @", "m.modified_by_user_id = @")]
    [TestCase(OwnedResourceKind.Query, true, "FROM [beacon].[Queries]", "[q].[OwnerUserId] = @", "[q].[CreatedByUserId] = @", "[q].[ModifiedByUserId] = @")]
    [TestCase(OwnedResourceKind.Subscription, true, "FROM [beacon].[Subscriptions]", "[s].[OwnerUserId] = @", "[s].[CreatedByUserId] = @", "[s].[ModifiedByUserId] = @")]
    [TestCase(OwnedResourceKind.DataContract, true, "FROM [beacon].[DataContracts]", "[d].[OwnerUserId] = @", "[d].[CreatedByUserId] = @", "[d].[ModifiedByUserId] = @")]
    [TestCase(OwnedResourceKind.Recipient, true, "FROM [beacon].[Recipients]", "[r].[CreatedByUserId] = @", "[r].[ModifiedByUserId] = @")]
    [TestCase(OwnedResourceKind.MigrationJob, true, "FROM [beacon].[MigrationJobs]", "[m].[CreatedByUserId] = @", "[m].[ModifiedByUserId] = @")]
    public async Task EachKind_IsMatchedByItsAttributionColumns_AsParameters_Translates(OwnedResourceKind kind, bool sqlServer, params string[] expected)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture())
            .ThenRow("ext-ada-subject")
            .AnsweringCountsWithOne();

        await Handler(capture.Factory(), new RecordingSecurityAudit(), TestPermissions.Holding(BeaconPermissions.Auditor))
            .Handle(new GetUserOwnedResourcesQuery { Id = 3, Kind = kind }, CancellationToken.None);

        capture.Commands.Should().HaveCount(3, "the user, a count, then the page");
        capture.Commands[0].Should().Contain(sqlServer ? "[beacon].[Users]" : "beacon.users").And.NotContain("archived_time IS NULL").And.NotContain("[ArchivedTime] IS NULL");
        foreach (var sql in capture.Commands.Skip(1))
        {
            foreach (var fragment in expected)
            {
                sql.Should().Contain(fragment);
            }

            sql.Should().NotContain("ext-ada-subject", "the external id is a parameter");
        }

        capture.CommandParameters[1].Values.Should().Contain("ext-ada-subject");
        if (kind is OwnedResourceKind.Recipient or OwnedResourceKind.MigrationJob)
        {
            capture.Commands[1].Should().NotContainEquivalentOf("owner_user_id").And.NotContain("[OwnerUserId]");
        }
    }

    [TestCase(OwnedResourceKind.Query, false, "q.archived_time IS NULL")]
    [TestCase(OwnedResourceKind.Subscription, false, "s.archived_time IS NULL")]
    [TestCase(OwnedResourceKind.DataContract, false, "d.archived_time IS NULL")]
    [TestCase(OwnedResourceKind.Recipient, false, "r.archived_time IS NULL")]
    [TestCase(OwnedResourceKind.MigrationJob, false, "m.archived_time IS NULL")]
    [TestCase(OwnedResourceKind.Query, true, "[q].[ArchivedTime] IS NULL")]
    [TestCase(OwnedResourceKind.Subscription, true, "[s].[ArchivedTime] IS NULL")]
    [TestCase(OwnedResourceKind.DataContract, true, "[d].[ArchivedTime] IS NULL")]
    [TestCase(OwnedResourceKind.Recipient, true, "[r].[ArchivedTime] IS NULL")]
    [TestCase(OwnedResourceKind.MigrationJob, true, "[m].[ArchivedTime] IS NULL")]
    public async Task OnlyLiveResources_AreListed_Translates(OwnedResourceKind kind, bool sqlServer, string liveFilter)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture())
            .ThenRow("ext-ada-subject")
            .AnsweringCountsWithOne();

        await Handler(capture.Factory(), new RecordingSecurityAudit(), TestPermissions.Holding(BeaconPermissions.Auditor))
            .Handle(new GetUserOwnedResourcesQuery { Id = 3, Kind = kind }, CancellationToken.None);

        foreach (var sql in capture.Commands.Skip(1))
        {
            sql.Should().Contain(liveFilter, "an archived resource is not listed: there is nothing to hand over");
        }
    }

    [Test]
    public async Task EachResource_SaysWhetherTheUserOwnsIt_CreatedIt_OrChangedItLast()
    {
        var changed = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        var store = new Store(
        [
            Store.Contract(1, "Owned only", 10, createdBy: "ext-other"),
            Store.Contract(2, "Created only", 10, owner: "ext-other", createdBy: "ext-ben", modifiedBy: "ext-other", modifiedTime: changed),
            Store.Contract(3, "Changed last only", 10, owner: "ext-other", createdBy: "ext-other", modifiedBy: "ext-ben", modifiedTime: changed),
            Store.Contract(4, "Someone else's", 10, owner: "ext-other", createdBy: "ext-other", modifiedBy: "ext-other", modifiedTime: changed),
            Store.Contract(5, "All three", 10, createdBy: "ext-ben", modifiedBy: "ext-ben", modifiedTime: changed)
        ]);

        var page = await Handler(store.Factory, new RecordingSecurityAudit(), TestPermissions.Holding(BeaconPermissions.Auditor))
            .Handle(new GetUserOwnedResourcesQuery { Id = 3, Kind = OwnedResourceKind.DataContract }, CancellationToken.None);

        page.TotalCount.Should().Be(4, "a contract the user neither owns, created nor changed last is not theirs to hand over");
        page.Items.Should().BeEquivalentTo(
            [
                Item(1, "Owned only", isOwner: true, isCreator: false, isLastModifier: false, null),
                Item(2, "Created only", isOwner: false, isCreator: true, isLastModifier: false, changed),
                Item(3, "Changed last only", isOwner: false, isCreator: false, isLastModifier: true, changed),
                Item(5, "All three", isOwner: true, isCreator: true, isLastModifier: true, changed)
            ],
            x => x.WithStrictOrdering());
    }

    [Test]
    public async Task AnUnknownUser_IsNotFound_AndNothingElseIsRead()
    {
        var capture = new SqlCapture().ThenNoRows();

        var act = () => Handler(capture.Factory(), new RecordingSecurityAudit(), TestPermissions.Holding(BeaconPermissions.Auditor))
            .Handle(new GetUserOwnedResourcesQuery { Id = 404, Kind = OwnedResourceKind.Query }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>().WithMessage("User 404 not found.");
        capture.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task AUserAdmin_ReadsTheNamesOfTheResourcesItsProjectsReach_AndOnlyIdAndKindOfTheOthers()
    {
        var store = new Store();
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(9, "ext-admin"));

        var page = await Handler(store.Factory, audit, TestPermissions.Holding(BeaconPermissions.UserAdmin), TestProjectAccess.ReadableIn(1))
            .Handle(new GetUserOwnedResourcesQuery { Id = 3, Kind = OwnedResourceKind.DataContract }, CancellationToken.None);

        page.Items.Should().BeEquivalentTo(
            [
                new { Id = 1, Name = "Orders contract", NameWithheld = false, IsOwner = true },
                new { Id = 2, Name = string.Empty, NameWithheld = true, IsOwner = true }
            ],
            x => x.ExcludingMissingMembers(),
            "contract 2 reads a data source only project 2 links");
        var read = audit.Single("ext-ben", "Payments contract");
        read.Action.Should().Be(SecurityAuditActions.OwnedResourcesRead);
        read.ResourceType.Should().Be(SecurityAuditResources.User);
        read.ResourceId.Should().Be("3");
        read.ActorUserId.Should().Be(9);
        read.DetailsJson.Should().Contain("\"kind\":\"DataContract\"");
    }

    [Test]
    public async Task AnAuditor_ReadsEveryName()
    {
        var store = new Store();

        var page = await Handler(store.Factory, new RecordingSecurityAudit(), TestPermissions.Holding(BeaconPermissions.Auditor), TestProjectAccess.ReadableIn(1))
            .Handle(new GetUserOwnedResourcesQuery { Id = 3, Kind = OwnedResourceKind.DataContract }, CancellationToken.None);

        page.Items.Select(x => x.Name).Should().Equal("Orders contract", "Payments contract");
        page.Items.Should().OnlyContain(x => !x.NameWithheld);
    }

    [Test]
    public async Task AReadThatCannotBeRecorded_IsRefused_AndReturnsNothing()
    {
        var store = new Store();
        var audit = new Mock<ISecurityAuditRecorder>();
        audit
            .Setup(x => x.StageAsync(It.IsAny<BeaconContext>(), It.IsAny<SecurityAuditEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is down"));

        var act = () => Handler(store.Factory, audit.Object, TestPermissions.Holding(BeaconPermissions.Auditor))
            .Handle(new GetUserOwnedResourcesQuery { Id = 3, Kind = OwnedResourceKind.DataContract }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static OwnedResourceItem Item(int id, string name, bool isOwner, bool isCreator, bool isLastModifier, DateTime? modifiedTime)
    {
        return new OwnedResourceItem
        {
            Id = id,
            Kind = OwnedResourceKind.DataContract,
            Name = name,
            IsOwner = isOwner,
            IsCreator = isCreator,
            IsLastModifier = isLastModifier,
            NameWithheld = false,
            CreatedTime = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc).AddDays(id),
            ModifiedTime = modifiedTime
        };
    }

    private static GetUserOwnedResourcesHandler Handler(
        IDbContextFactory<BeaconContext> factory,
        ISecurityAuditRecorder audit,
        IBeaconPermissionAccessor permissions,
        ProjectAccessScope? scope = null)
    {
        return new GetUserOwnedResourcesHandler(
            factory,
            permissions,
            TestProjectAccess.Evaluator(scope ?? TestProjectAccess.Unrestricted),
            SecurityAuditTestKit.Reads(factory, audit));
    }

    // User 3 (ext-ben) owns two contracts by default: one on data source 10, which project 1 links, one on data source 20,
    // which only project 2 links.
    private sealed class Store
    {
        public Store(List<DataContract>? contracts = null)
        {
            var saved = new List<object>();
            var users = new List<BeaconUser> { new() { Id = 3, ExternalId = "ext-ben", UserName = "ben" } };
            contracts ??=
            [
                Contract(1, "Orders contract", 10),
                Contract(2, "Payments contract", 20)
            ];
            var links = new List<ProjectDataSource>
            {
                new() { Id = 1, ProjectId = 1, DataSourceId = 10 },
                new() { Id = 2, ProjectId = 2, DataSourceId = 20 }
            };
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, saved),
                        [typeof(DataContract)] = RecordingBeaconContext.MemorySet(contracts, saved),
                        [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, saved)
                    },
                    saved));
            Factory = factory.Object;
        }

        public IDbContextFactory<BeaconContext> Factory { get; }

        public static DataContract Contract(
            int id,
            string name,
            int dataSourceId,
            string owner = "ext-ben",
            string? createdBy = null,
            string? modifiedBy = null,
            DateTime? modifiedTime = null)
        {
            return new DataContract
            {
                Id = id,
                Name = name,
                DataSourceId = dataSourceId,
                SchemaName = "public",
                TableName = "orders",
                CronExpression = "0 * * * *",
                OwnerUserId = owner,
                CreatedByUserId = createdBy,
                ModifiedByUserId = modifiedBy,
                ModifiedTime = modifiedTime,
                CreatedTime = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc).AddDays(id)
            };
        }
    }
}
