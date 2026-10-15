using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataQuality.CreateDataContract;
using Beacon.Core.Handlers.DataQuality.DeleteDataContract;
using Beacon.Core.Handlers.DataQuality.EvaluateDataContract;
using Beacon.Core.Handlers.DataQuality.UpdateDataContract;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Models.DataSources;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.ChangeGovernance;
using Beacon.Tests.Unit.Notifications;
using Beacon.Core.Models;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The events of the resources that read or move data: a data source's create and delete (its type, engine and projects,
/// never its connection), a data contract's create, change, archive and manual evaluation (rule counts and scores, never a
/// rule's SQL), a migration job's create and delete (its sources and mode, never its query or table), each with the change
/// request it waits on and in the unit of work of its change; and the creator and last modifier those resources and a
/// query record, taken from the caller.
/// </summary>
[TestFixture]
public class DataResourceEventTests
{
    private const string Ada = "ext-ada";
    private const int AdaId = 7;
    private const string RuleSql = "SELECT count(*) FROM public.orders WHERE paid_at IS NULL";
    private static readonly DateTime Now = new(2026, 10, 14, 9, 30, 0, DateTimeKind.Utc);

    private RecordingSecurityAudit _audit = null!;
    private FakeTimeProvider _time = null!;

    [SetUp]
    public void SetUp()
    {
        _audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(AdaId, Ada, isAdmin: true));
        _time = new FakeTimeProvider(new DateTimeOffset(Now));
    }

    [Test]
    public async Task CreatingADataSource_RecordsItsTypeEngineAndProjects_NeverItsConnection()
    {
        var saved = new List<object>();
        var service = DataSources(saved, []);

        await service.CreateDataSource(
            new DataSourceData { Name = "warehouse", ConnectionString = "Host=db.internal;Password=hunter2", DatabaseEngineType = DatabaseEngineType.PostgreSQL, ProjectIds = [4, 4, 2] },
            CancellationToken.None);

        var created = _audit.Single("db.internal", "hunter2", "warehouse");
        created.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.DataSourceCreated, ActorUserId = (int?)AdaId, ResourceType = SecurityAuditResources.DataSource },
            x => x.ExcludingMissingMembers());
        created.DetailsJson.Should().Be("{\"dataSourceType\":\"Database\",\"databaseEngineType\":\"PostgreSQL\",\"projectIds\":[4,2]}");
        _audit.Ways.Should().Equal("saved");
    }

    [Test]
    public async Task DeletingADataSource_StagesDataSourceDeleted_WithTheArchive()
    {
        var saved = new List<object>();
        var source = new DataSource { Id = 12, Name = "warehouse", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc", QuerySteps = [] };
        var service = DataSources(saved, [source]);

        await service.DeleteDataSource(source.Id, CancellationToken.None);

        _audit.Single("warehouse").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.DataSourceDeleted, ResourceId = "12", DetailsJson = (string?)null },
            x => x.ExcludingMissingMembers());
        _audit.Ways.Should().Equal("staged");
        source.ArchivedTime.Should().NotBeNull();
    }

    [Test]
    public async Task ChangingADataSourcesConnection_StagesCredentialsRotated_WithTheFieldsChanged_NeverAValue()
    {
        var saved = new List<object>();
        var source = new DataSource { Id = 12, Name = "warehouse", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc", MetadataMaxTables = 50 };
        var service = DataSources(saved, [source], storedConnection: "Host=db.internal;Password=hunter2");

        await service.UpdateDataSource(
            new DataSourceData { DataSourceId = 12, Name = "warehouse-eu", ConnectionString = "Host=db.internal;Password=rotated-secret", MetadataMaxTables = 50 },
            CancellationToken.None);

        var rotated = _audit.Single("hunter2", "rotated-secret", "db.internal", "warehouse");
        rotated.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.CredentialsRotated, ResourceType = SecurityAuditResources.DataSource, ResourceId = "12" },
            x => x.ExcludingMissingMembers());
        rotated.DetailsJson.Should().Be("{\"connectionChanged\":true,\"changedFields\":[\"Name\"]}");
        _audit.Ways.Should().Equal("staged");
    }

    [Test]
    public async Task ChangingADataSourceKeepingItsConnection_StagesDataSourceUpdated_AndAnEditThatChangesNothing_StagesNothing()
    {
        var saved = new List<object>();
        var source = new DataSource { Id = 12, Name = "warehouse", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc", MetadataMaxTables = 50 };
        var service = DataSources(saved, [source], storedConnection: "Host=db.internal;Password=hunter2");
        var edit = new DataSourceData { DataSourceId = 12, Name = "warehouse", ConnectionString = "Host=db.internal;Password=hunter2", MetadataMaxTables = 80 };

        await service.UpdateDataSource(edit, CancellationToken.None);
        await service.UpdateDataSource(edit, CancellationToken.None);

        var updated = _audit.Single("hunter2", "db.internal");
        updated.Action.Should().Be(SecurityAuditActions.DataSourceUpdated);
        updated.DetailsJson.Should().Be("{\"connectionChanged\":false,\"changedFields\":[\"MetadataMaxTables\"]}");
    }

    [Test]
    public async Task CreatingAContract_RecordsItsRuleCountsAndRecipients_AndItsCreator()
    {
        var store = new ChangeApprovalStore();

        var created = await CreateContract(store, new BeaconConfiguration()).Handle(CreateCommand(Volume()), CancellationToken.None);

        var contractCreated = _audit.Single("orders contract");
        contractCreated.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.ContractCreated, ResourceType = SecurityAuditResources.DataContract, ResourceId = created.DataContractId.ToString() },
            x => x.ExcludingMissingMembers());
        contractCreated.DetailsJson.Should().Be(
            "{\"ruleCount\":1,\"customSqlRuleCount\":0,\"recipientIds\":[],\"isEnabled\":false,\"changeApprovalId\":null,\"dataSourceId\":3}");
        store.Saved.OfType<DataContract>().Single().CreatedByUserId.Should().Be(Ada);
    }

    [Test]
    public async Task CreatingAContractWithCustomSql_UnderTheWorkflow_RecordsItsRequest_NeverTheRuleSql()
    {
        var store = new ChangeApprovalStore();

        await CreateContract(store, TestChangeApprovals.On()).Handle(CreateCommand(CustomSql()), CancellationToken.None);
        store.Commit();

        var request = store.Requests.Should().ContainSingle().Subject;
        _audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.ContractCreated, SecurityAuditActions.ChangeSubmitted);
        _audit.Events[0].DetailsJson.Should().Contain("\"customSqlRuleCount\":1").And.Contain($"\"changeApprovalId\":{request.Id}");
        _audit.Events[1].DetailsJson.Should().StartWith("{\"kind\":\"CustomSqlRules\"");
        foreach (var auditEvent in _audit.Events)
        {
            SecurityAuditTestKit.AssertNoPii(auditEvent, RuleSql, "paid_at", "orders contract");
        }

        store.Offline.Commits.Should().Be(1);
    }

    [Test]
    public async Task ChangingAContract_RecordsContractUpdated_AndWhoChangedIt()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.Volume());

        await UpdateContract(store).Handle(
            new UpdateDataContractCommand(contract.Id, ChangeApprovalStore.Ledger, "public", "orders", contract.Name, null, contract.CronExpression, true, false, 80, [Volume()]),
            CancellationToken.None);

        var updated = _audit.Single("orders contract");
        updated.Action.Should().Be(SecurityAuditActions.ContractUpdated);
        updated.DetailsJson.Should().Be(
            "{\"ruleCount\":1,\"customSqlRuleCount\":0,\"recipientIds\":[],\"isEnabled\":true,\"changeApprovalId\":null," +
            "\"previousDataSourceId\":3,\"dataSourceId\":4}");
        contract.ModifiedByUserId.Should().Be(Ada);
        contract.ModifiedTime.Should().Be(Now);
    }

    [Test]
    public async Task DeletingAContract_StagesContractArchived_AndWhoArchivedIt()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.Volume());
        contract.OwnerUserId = Ada;
        var handler = new DeleteDataContractHandler(
            store.Factory(),
            Mock.Of<IBeaconScheduler>(),
            NotificationTestKit.UserContext(NotificationTestKit.Admin()),
            Caller(),
            _audit,
            _time,
            NullLogger<DeleteDataContractHandler>.Instance);

        await handler.Handle(new DeleteDataContractCommand(contract.Id), CancellationToken.None);

        _audit.Single().Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.ContractArchived, ResourceId = contract.Id.ToString() },
            x => x.ExcludingMissingMembers());
        _audit.Ways.Should().Equal("staged");
        contract.ModifiedByUserId.Should().Be(Ada);
    }

    [Test]
    public async Task EvaluatingAContract_RecordsTheCountsAndTheRoundedScore_AndARefusedEvaluationRecordsNothing()
    {
        var store = new ChangeApprovalStore();
        var contract = store.AddContract(ChangeApprovalStore.Volume());
        contract.OwnerUserId = "ext-someone-else";
        var evaluation = new Mock<IDataQualityEvaluationService>();
        evaluation
            .Setup(x => x.EvaluateContractAsync(contract.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DataQualityEvaluationData { Id = 88, DataContractId = contract.Id, OverallScore = 66.5, PassedRules = 2, FailedRules = 1, TotalRules = 4, AwaitingApprovalRules = 1 });

        var notTheOwner = () => Evaluate(store, evaluation.Object, TestChangeApprovals.Caller("ext-editor", 5)).Handle(new EvaluateDataContractCommand(contract.Id), CancellationToken.None);
        await notTheOwner.Should().ThrowAsync<BeaconForbiddenException>();
        _audit.Events.Should().BeEmpty();

        await Evaluate(store, evaluation.Object, Caller()).Handle(new EvaluateDataContractCommand(contract.Id), CancellationToken.None);

        var evaluated = _audit.Single();
        evaluated.Action.Should().Be(SecurityAuditActions.ContractEvaluated);
        evaluated.DetailsJson.Should().Be(
            "{\"evaluationId\":88,\"score\":67,\"passedRules\":2,\"failedRules\":1,\"totalRules\":4,\"awaitingApprovalRules\":1}");
        _audit.Ways.Should().Equal("recorded");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CreatingAMigrationJob_RecordsItsSourcesAndMode_NeverItsQuery_AndItsCreator(bool workflow)
    {
        var store = new ChangeApprovalStore();
        var configuration = workflow ? TestChangeApprovals.On() : new BeaconConfiguration();

        var response = await Migrations(store, configuration).CreateMigrationJob(
            new CreateMigrationJobRequest
            {
                Name = "copy orders",
                DataSourceId = ChangeApprovalStore.Warehouse,
                QueryText = "SELECT id, total FROM public.orders",
                DestinationDataSourceId = ChangeApprovalStore.Target,
                DestinationTable = "orders_copy"
            },
            CancellationToken.None);
        store.Commit();

        response.Success.Should().BeTrue(response.ErrorMessage);
        var created = _audit.Of(SecurityAuditActions.MigrationJobCreated).Should().ContainSingle().Subject;
        SecurityAuditTestKit.AssertNoPii(created, "orders_copy", "copy orders", "total");
        created.ResourceId.Should().Be(response.MigrationJobId.ToString());
        created.DetailsJson.Should().StartWith("{\"sourceDataSourceId\":3,\"destinationDataSourceId\":5,\"mode\":\"Insert\",\"isEnabled\":true,\"changeApprovalId\":");
        _audit.Of(SecurityAuditActions.ChangeSubmitted).Should().HaveCount(workflow ? 1 : 0);
        store.Jobs.Single().CreatedByUserId.Should().Be(Ada);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeletingAMigrationJob_StagesMigrationJobDeleted(bool force)
    {
        var store = new ChangeApprovalStore();
        var job = store.AddJob();

        (await Migrations(store, new BeaconConfiguration()).DeleteMigrationJob(job.Id, CancellationToken.None, force)).Success.Should().BeTrue();

        var deleted = _audit.Single("copy orders", "SELECT 1");
        deleted.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.MigrationJobDeleted, ResourceId = job.Id.ToString(), DetailsJson = $"{{\"forceDelete\":{(force ? "true" : "false")}}}" },
            x => x.ExcludingMissingMembers());
        _audit.Ways.Should().Equal("staged");
        job.ModifiedByUserId.Should().Be(force ? null : Ada, "an archived job records who archived it; a removed one is gone");
    }

    [Test]
    public async Task CreatingAQuery_RecordsTheCallerAsItsCreator()
    {
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object>(), saved));

        await new CreateQueryHandler(factory.Object, Caller()).Handle(new CreateQueryCommand { Name = "Late payments" }, CancellationToken.None);

        saved.OfType<Query>().Single().Should().BeEquivalentTo(new { OwnerUserId = Ada, CreatedByUserId = Ada, ModifiedByUserId = (string?)null }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task SavingAQuery_WithoutTheWorkflow_RecordsTheCallerAsItsLastModifier()
    {
        var store = new GovernanceStore();
        store.Activate(store.AddVersion(1, QueryVersionStatus.Active));
        var service = TestQueryServices.QueryService(
            store.Factory(),
            versions: TestQueryServices.VersionService(store.Factory()),
            configuration: new BeaconConfiguration(),
            actors: Caller());

        (await service.UpdateQuery(GovernanceStore.Edit("SELECT 2"), CancellationToken.None)).Success.Should().BeTrue();

        store.Query.ModifiedByUserId.Should().Be(Ada);
        store.Query.ModifiedTime.Should().NotBeNull();
        store.Query.CreatedByUserId.Should().BeNull("a query created before attribution was recorded keeps no creator");
    }

    [Test]
    public async Task ArchivingAQuery_RecordsTheCallerOnItAndOnTheSubscriptionsItArchives()
    {
        var subscription = new Subscription { Id = 60, QueryId = 9, CronExpression = "0 * * * *" };
        var query = new Query { Id = 9, Name = "Orders", Steps = [], Subscriptions = [subscription] };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(new Dictionary<Type, object> { [typeof(Query)] = RecordingBeaconContext.MemorySet(new List<Query> { query }, []) }, []));

        await TestQueryServices.QueryService(factory.Object, actors: Caller()).DeleteQuery(9, CancellationToken.None);

        query.Should().BeEquivalentTo(new { ModifiedByUserId = Ada }, x => x.ExcludingMissingMembers());
        query.ArchivedTime.Should().NotBeNull();
        subscription.ModifiedByUserId.Should().Be(Ada, "archiving a query archives its subscriptions, by the same caller");
    }

    private static IBeaconActorAccessor Caller() => RecordingSecurityAudit.Accessor(RecordingSecurityAudit.User(AdaId, Ada, isAdmin: true));

    private static DataContractRuleData Volume() =>
        new()
        {
            Name = "volume",
            RuleType = DataContractRuleType.Volume,
            Configuration = """{"minRows":1}""",
            IsEnabled = true
        };

    private static DataContractRuleData CustomSql() =>
        new()
        {
            Name = "unpaid",
            RuleType = DataContractRuleType.CustomSql,
            Configuration = $$"""{"sql":{{System.Text.Json.JsonSerializer.Serialize(RuleSql)}}}""",
            IsEnabled = true
        };

    private static CreateDataContractCommand CreateCommand(params DataContractRuleData[] rules) =>
        new(ChangeApprovalStore.Warehouse, "public", "orders", "orders contract", null, "0 * * * *", false, false, 80, [.. rules]);

    private DataSourceService DataSources(List<object> saved, List<DataSource> sources, string? storedConnection = null)
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet(sources, saved),
            [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet(new List<ColumnMetadata>(), saved),
            [typeof(Beacon.Core.Data.Entities.DataMigration.MigrationJob)] = RecordingBeaconContext.MemorySet(new List<Beacon.Core.Data.Entities.DataMigration.MigrationJob>(), saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns("enc");
        encryption.Setup(x => x.Decrypt("enc")).Returns(storedConnection!);

        return new DataSourceService(
            factory.Object,
            encryption.Object,
            Mock.Of<IDataSourceProviderFactory>(),
            Mock.Of<IManualQueryExecutionLogger>(),
            Mock.Of<IBeaconUserContext>(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            TestQueryServices.Runner(),
            _audit,
            NullLogger<DataSourceService>.Instance);
    }

    private CreateDataContractHandler CreateContract(ChangeApprovalStore store, BeaconConfiguration configuration) =>
        new(
            store.Factory(),
            Mock.Of<IBeaconScheduler>(),
            NotificationTestKit.UserContext(NotificationTestKit.Admin()),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            Caller(),
            configuration,
            TestChangeApprovals.CustomSql(configuration, Caller()),
            _audit);

    private UpdateDataContractHandler UpdateContract(ChangeApprovalStore store) =>
        new(
            store.Factory(),
            Mock.Of<IBeaconScheduler>(),
            NotificationTestKit.UserContext(NotificationTestKit.Admin()),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            Caller(),
            TestChangeApprovals.CustomSql(new BeaconConfiguration(), Caller()),
            _audit,
            _time,
            NullLogger<UpdateDataContractHandler>.Instance);

    private EvaluateDataContractHandler Evaluate(ChangeApprovalStore store, IDataQualityEvaluationService evaluation, IBeaconActorAccessor caller) =>
        new(evaluation, store.Factory(), caller, _audit, NullLogger<EvaluateDataContractHandler>.Instance);

    private MigrationService Migrations(ChangeApprovalStore store, BeaconConfiguration configuration) =>
        new(
            store.Factory(),
            Mock.Of<IEncryptionService>(),
            Mock.Of<IQueryService>(MockBehavior.Strict),
            Mock.Of<IQueryExecutionPreviewService>(MockBehavior.Strict),
            Mock.Of<IBeaconUserContext>(),
            new MigrationBulkContextFactory([]),
            NullLogger<MigrationService>.Instance,
            Options.Create(new MigrationOptions { Enabled = true }),
            TestChangeApprovals.Submitter(configuration, TestChangeApprovals.Caller(Ada, AdaId)),
            Caller(),
            _audit,
            _time);
}
