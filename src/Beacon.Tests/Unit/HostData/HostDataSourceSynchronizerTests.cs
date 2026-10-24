using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostData;

[TestFixture]
public class HostDataSourceSynchronizerTests
{
    private const string ReadOnlyConnectionString = "Server=db;Database=Netgiro;User Id=beacon_ro;Password=Sup3rS3cret!";

    [Test]
    public async Task SyncAsync_FirstRun_CreatesProjectDataSourceLinkAndMetadata()
    {
        var store = new HostSyncStore();
        var relationships = new Mock<ISchemaRelationshipSyncService>();
        var (synchronizer, registration) = Build(store, x => x.AllowTables("Customer", "lending.Loan"), relationships);

        var outcome = await synchronizer.SyncAsync(registration, CancellationToken.None);

        outcome.Created.Should().BeTrue();
        outcome.MetadataRewritten.Should().BeTrue();
        var dataSource = store.DataSources.Should().ContainSingle().Subject;
        dataSource.HostManagedKey.Should().Be("efcore:Netgiro");
        dataSource.Name.Should().Be("Netgiro");
        dataSource.IsReadOnly.Should().BeTrue();
        dataSource.DatabaseEngineType.Should().Be(DatabaseEngineType.MSSQL);
        dataSource.HostModelHash.Should().NotBeNullOrEmpty();
        store.Projects.Should().ContainSingle(x => x.Name == "Netgiro Admin");
        store.ProjectDataSources.Should().ContainSingle(x => x.DataSourceId == dataSource.Id && x.ProjectId == store.Projects[0].Id);
        store.Tables
            .Select(x => x.TableName)
            .Should()
            .BeEquivalentTo("Customer", "Loan");
        store.Tables.Should().OnlyContain(x => x.DataSourceId == dataSource.Id);
        store.Projects[0].HostManagedKey.Should().Be("host:netgiro admin");
        store.SaveCount.Should().Be(2, "the host project is ensured in its own unit of work, then the data source");
        relationships.Verify(x => x.SyncAsync(dataSource.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SyncAllAsync_ChecksTheReadOnlyLogin_OnceWithTheExposedTables_AfterTheSync()
    {
        var store = new HostSyncStore();
        var saveCountAtCheck = -1;
        var loginCheck = new RecordingHostLoginCheck { OnCall = () => saveCountAtCheck = store.SaveCount };
        var (synchronizer, _) = Build(store, x => x.AllowTables("Customer", "lending.Loan"), loginCheck: loginCheck);

        await synchronizer.SyncAllAsync(CancellationToken.None);

        var call = loginCheck.Calls.Should().ContainSingle().Subject;
        call.DataSourceId.Should().Be(store.DataSources.Single().Id);
        call.Tables
            .Select(x => x.Name)
            .Should()
            .BeEquivalentTo("Customer", "Loan");
        saveCountAtCheck.Should().Be(2, "the login is checked once the source is saved");
    }

    [Test]
    public async Task SyncAsync_AloneDoesNotCheckTheLogin()
    {
        var store = new HostSyncStore();
        var loginCheck = new RecordingHostLoginCheck();
        var (synchronizer, registration) = Build(store, x => x.AllowTables("Customer"), loginCheck: loginCheck);

        await synchronizer.SyncAsync(registration, CancellationToken.None);

        loginCheck.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task SyncAllAsync_WhenTheLoginCheckThrows_KeepsTheSync_AndWarnsWithTheIdOnly()
    {
        var store = new HostSyncStore();
        var logs = new LogRecorder();
        var loginCheck = new RecordingHostLoginCheck { Failure = new InvalidOperationException("login beacon_ro on db failed") };
        var (synchronizer, _) = Build(store, x => x.AllowTables("Customer", "lending.Loan"), logs: logs, loginCheck: loginCheck);

        var act = () => synchronizer.SyncAllAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        var dataSource = store.DataSources.Should().ContainSingle().Subject;
        store.ProjectDataSources.Should().ContainSingle(x => x.DataSourceId == dataSource.Id);
        store.Tables.Should().HaveCount(2);
        var warning = logs.Entries.Should().ContainSingle(x => x.Message.Contains("could not verify")).Subject;
        warning.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
        warning.FullText.Should().Contain(dataSource.Id.ToString()).And.NotContain("beacon_ro");
    }

    [Test]
    public async Task SyncAllAsync_WhenTheLoginCheckWarns_TheSyncIsTheSame()
    {
        var checkedStore = new HostSyncStore();
        var uncheckedStore = new HostSyncStore();
        var (withWarning, _) = Build(
            checkedStore,
            x => x.AllowTables("Customer", "lending.Loan"),
            loginCheck: new RecordingHostLoginCheck { OnCall = () => { } });
        var (withoutWarning, registration) = Build(uncheckedStore, x => x.AllowTables("Customer", "lending.Loan"));

        await withWarning.SyncAllAsync(CancellationToken.None);
        var outcome = await withoutWarning.SyncAsync(registration, CancellationToken.None);

        checkedStore.DataSources.Should().ContainSingle().Which.Id.Should().Be(outcome.DataSourceId);
        checkedStore.Tables
            .Select(x => x.TableName)
            .Should()
            .BeEquivalentTo(uncheckedStore.Tables.Select(x => x.TableName));
        checkedStore.SaveCount.Should().Be(uncheckedStore.SaveCount);
    }

    [Test]
    public async Task SyncAsync_PersistsOnlyAnEncryptedConnectionReference()
    {
        var store = new HostSyncStore();
        var (synchronizer, registration) = Build(store, x => x.AllowTables("Customer"));

        await synchronizer.SyncAsync(registration, CancellationToken.None);

        var stored = store.DataSources.Single().EncryptedConnectionData;
        stored.Should().StartWith(FakeEncryption.Prefix);
        stored.Should().NotContain("Sup3rS3cret").And.NotContain("beacon_ro");
        FakeEncryption.Instance.Decrypt(stored).Should().Be("{\"hostConnectionStringName\":\"BeaconReadOnly\"}");
    }

    // The host's model is the stored catalog of a host source: a sync that rewrites it drops the source's catalog snapshots,
    // a sync that leaves it as it was does not.
    [Test]
    public async Task SyncAsync_InvalidatesTheCatalogSnapshots_OnlyWhenItRewritesTheMetadata()
    {
        var store = new HostSyncStore();
        var snapshots = new Mock<ICatalogSnapshotCache>();
        var (synchronizer, registration) = Build(store, x => x.AllowTables("Customer", "lending.Loan"), snapshots: snapshots.Object);

        var first = await synchronizer.SyncAsync(registration, CancellationToken.None);
        await synchronizer.SyncAsync(registration, CancellationToken.None);

        snapshots.Verify(x => x.Invalidate(first.DataSourceId), Times.Once);
    }

    [Test]
    public async Task SyncAsync_SecondRunWithSameModel_SkipsMetadataRewrite()
    {
        var store = new HostSyncStore();
        var relationships = new Mock<ISchemaRelationshipSyncService>();
        var (synchronizer, registration) = Build(store, x => x.AllowTables("Customer", "lending.Loan"), relationships);

        await synchronizer.SyncAsync(registration, CancellationToken.None);
        var tableIds = store.Tables.Select(x => x.Id).ToList();
        var second = await synchronizer.SyncAsync(registration, CancellationToken.None);

        second.Created.Should().BeFalse();
        second.MetadataRewritten.Should().BeFalse();
        store.DataSources.Should().ContainSingle();
        store.Projects.Should().ContainSingle();
        store.ProjectDataSources.Should().ContainSingle();
        store.Tables.Select(x => x.Id).Should().Equal(tableIds);
        relationships.Verify(x => x.SyncAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SyncAsync_UserProjectWithTheSameName_IsNeverLinkedToTheHostDatabase()
    {
        var store = new HostSyncStore();
        var userProject = new Beacon.Core.Data.Entities.Projects.Project { Id = 1, Name = "Netgiro Admin" };
        store.Projects.Add(userProject);
        var (synchronizer, registration) = Build(store, x => x.AllowTables("Customer"));

        await synchronizer.SyncAsync(registration, CancellationToken.None);

        var hostProject = store.Projects.Single(x => x.HostManagedKey != null);
        hostProject.Id.Should().NotBe(userProject.Id);
        hostProject.Name.Should().Be("Netgiro Admin (host)", "the user's project keeps its name and the host project is disambiguated");
        store.ProjectDataSources.Should().ContainSingle()
            .Which.ProjectId.Should().Be(hostProject.Id);
        userProject.HostManagedKey.Should().BeNull();
    }

    [Test]
    public async Task SyncAsync_HostProjectRenamedByAUser_IsStillFoundByItsKey()
    {
        var store = new HostSyncStore();
        var (synchronizer, registration) = Build(store, x => x.AllowTables("Customer"));
        await synchronizer.SyncAsync(registration, CancellationToken.None);
        store.Projects.Single().Name = "Renamed in the UI";

        await synchronizer.SyncAsync(registration, CancellationToken.None);

        store.Projects.Should().ContainSingle();
        store.ProjectDataSources.Should().ContainSingle();
    }

    [Test]
    public async Task SyncAsync_ProjectNameChanged_RemovesTheOldProjectsLink()
    {
        var store = new HostSyncStore();
        var (first, firstRegistration) = Build(store, x => x.AllowTables("Customer"));
        await first.SyncAsync(firstRegistration, CancellationToken.None);
        var oldProjectId = store.Projects.Single().Id;

        var (second, secondRegistration) = Build(store, x => x.AllowTables("Customer"), projectName: "Netgiro Ops");
        await second.SyncAsync(secondRegistration, CancellationToken.None);

        var newProject = store.Projects.Single(x => x.HostManagedKey == "host:netgiro ops");
        store.ProjectDataSources.Should().ContainSingle("the old project must lose access to the host database")
            .Which.ProjectId.Should().Be(newProject.Id);
        store.ProjectDataSources.Should().NotContain(x => x.ProjectId == oldProjectId);
    }

    [Test]
    public async Task SyncAsync_ProjectNameChangedAwayFromARestrictedProject_RestrictsTheHostProject_AndCarriesItsMembers()
    {
        var store = new HostSyncStore();
        var (first, firstRegistration) = Build(store, x => x.AllowTables("Customer"));
        await first.SyncAsync(firstRegistration, CancellationToken.None);
        var oldProject = store.Projects.Single();
        oldProject.RequiresMembership = true;
        store.ProjectMembers.Add(new ProjectMember { ProjectId = oldProject.Id, UserId = 3, Role = ProjectRole.ProjectAdmin });
        store.ProjectMembers.Add(new ProjectMember { ProjectId = oldProject.Id, UserId = 4, Role = ProjectRole.Viewer });

        var (second, secondRegistration) = Build(store, x => x.AllowTables("Customer"), projectName: "Netgiro Ops");
        await second.SyncAsync(secondRegistration, CancellationToken.None);

        var newProject = store.Projects.Single(x => x.HostManagedKey == "host:netgiro ops");
        newProject.RequiresMembership.Should().BeTrue("moving the source must not open it");
        store.ProjectMembers
            .Where(x => x.ProjectId == newProject.Id)
            .Select(x => (x.UserId, x.Role))
            .Should().BeEquivalentTo([(3, ProjectRole.ProjectAdmin), (4, ProjectRole.Viewer)]);
        store.ProjectDataSources.Should().ContainSingle().Which.ProjectId.Should().Be(newProject.Id);
    }

    [Test]
    public async Task SyncAsync_ProjectNameChangedAwayFromARestrictedProject_WhileTheHostManagesMembership_DoesNotRelink()
    {
        var store = new HostSyncStore();
        var (first, firstRegistration) = Build(store, x => x.AllowTables("Customer"));
        await first.SyncAsync(firstRegistration, CancellationToken.None);
        var oldProject = store.Projects.Single();
        oldProject.RequiresMembership = true;
        var logs = new LogRecorder();

        var (second, secondRegistration) = Build(
            store,
            x => x.AllowTables("Customer"),
            projectName: "Netgiro Ops",
            membershipSource: Mock.Of<IProjectMembershipSource>(x => x.IsAvailable == true),
            logs: logs);
        await second.SyncAsync(secondRegistration, CancellationToken.None);

        var newProject = store.Projects.Single(x => x.HostManagedKey == "host:netgiro ops");
        newProject.RequiresMembership.Should().BeFalse();
        store.ProjectDataSources.Should().ContainSingle("the source stays in the restricted project, never opened")
            .Which.ProjectId.Should().Be(oldProject.Id);
        logs.Entries.Should().Contain(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Error && x.Message.Contains("was not moved"));
    }

    [Test]
    public async Task SyncAsync_SourceHeldByTwoRestrictedProjects_IsNotRelinked()
    {
        var store = new HostSyncStore();
        var (first, firstRegistration) = Build(store, x => x.AllowTables("Customer"));
        await first.SyncAsync(firstRegistration, CancellationToken.None);
        var oldProject = store.Projects.Single();
        oldProject.RequiresMembership = true;
        var other = new Project { Id = 900, Name = "Finance", RequiresMembership = true };
        store.Projects.Add(other);
        store.ProjectDataSources.Add(new ProjectDataSource { ProjectId = other.Id, DataSourceId = store.DataSources.Single().Id });

        var (second, secondRegistration) = Build(store, x => x.AllowTables("Customer"), projectName: "Netgiro Ops");
        await second.SyncAsync(secondRegistration, CancellationToken.None);

        var newProject = store.Projects.Single(x => x.HostManagedKey == "host:netgiro ops");
        store.ProjectDataSources.Select(x => x.ProjectId).Should().BeEquivalentTo([oldProject.Id, other.Id]);
        store.ProjectDataSources.Should().NotContain(x => x.ProjectId == newProject.Id);
    }

    [Test]
    public async Task SyncAsync_ProjectNameChangedAwayFromAnArchivedRestrictedProject_RestrictsTheHostProject_WithoutItsMembers()
    {
        var store = new HostSyncStore();
        var (first, firstRegistration) = Build(store, x => x.AllowTables("Customer"));
        await first.SyncAsync(firstRegistration, CancellationToken.None);
        var oldProject = store.Projects.Single();
        oldProject.RequiresMembership = true;
        oldProject.ArchivedTime = DateTime.UtcNow;
        store.ProjectMembers.Add(new ProjectMember { ProjectId = oldProject.Id, UserId = 3, Role = ProjectRole.ProjectAdmin });

        var (second, secondRegistration) = Build(store, x => x.AllowTables("Customer"), projectName: "Netgiro Ops");
        await second.SyncAsync(secondRegistration, CancellationToken.None);

        var newProject = store.Projects.Single(x => x.HostManagedKey == "host:netgiro ops");
        newProject.RequiresMembership.Should().BeTrue("the source was reachable by Admins only, and must not be opened by the move");
        store.ProjectMembers.Should().NotContain(x => x.ProjectId == newProject.Id, "an archived project's members held nothing there");
        var link = store.ProjectDataSources.Should().ContainSingle().Subject;
        link.ProjectId.Should().Be(newProject.Id);
        link.LinkedByUserId.Should().BeNull("the host links its own source; no user did");
    }

    [Test]
    public async Task SyncAsync_HostProjectWithMembersTheRestrictedProjectLacks_IsNotRelinked()
    {
        var store = new HostSyncStore();
        var (first, firstRegistration) = Build(store, x => x.AllowTables("Customer"));
        await first.SyncAsync(firstRegistration, CancellationToken.None);
        var oldProject = store.Projects.Single();
        oldProject.RequiresMembership = true;
        store.ProjectMembers.Add(new ProjectMember { ProjectId = oldProject.Id, UserId = 3, Role = ProjectRole.ProjectAdmin });
        var hostProject = new Project { Id = 950, Name = "Netgiro Ops", HostManagedKey = "host:netgiro ops" };
        store.Projects.Add(hostProject);
        store.ProjectMembers.Add(new ProjectMember { ProjectId = hostProject.Id, UserId = 3, Role = ProjectRole.Viewer });
        store.ProjectMembers.Add(new ProjectMember { ProjectId = hostProject.Id, UserId = 9, Role = ProjectRole.Editor });
        var logs = new LogRecorder();

        var (second, secondRegistration) = Build(store, x => x.AllowTables("Customer"), projectName: "Netgiro Ops", logs: logs);
        await second.SyncAsync(secondRegistration, CancellationToken.None);

        hostProject.RequiresMembership.Should().BeFalse();
        store.ProjectDataSources.Should().ContainSingle("user 9 would gain the restricted source")
            .Which.ProjectId.Should().Be(oldProject.Id);
        store.ProjectMembers.Where(x => x.ProjectId == hostProject.Id).Should().HaveCount(2);
        logs.Entries.Should().Contain(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Error && x.Message.Contains("1 member(s)"));
    }

    [Test]
    public async Task SyncAsync_TableRemovedFromAllowList_IsArchived_AndKeptRowsKeepIdentity()
    {
        var store = new HostSyncStore();
        var (first, firstRegistration) = Build(store, x => x.AllowTables("Customer", "lending.Loan"));
        await first.SyncAsync(firstRegistration, CancellationToken.None);
        var customerId = store.Tables.Single(x => x.TableName == "Customer").Id;

        var (second, secondRegistration) = Build(store, x => x.AllowTables("Customer").ExcludeColumns(y => y.Name == "Name"));
        var outcome = await second.SyncAsync(secondRegistration, CancellationToken.None);

        outcome.MetadataRewritten.Should().BeTrue();
        store.Tables.Single(x => x.TableName == "Loan").ArchivedTime.Should().NotBeNull();
        var customer = store.Tables.Single(x => x.TableName == "Customer");
        customer.Id.Should().Be(customerId);
        customer.ArchivedTime.Should().BeNull();
        customer.Columns.Should().NotContain(x => x.ColumnName == "Name");
        store.RemovedColumns.Should().Contain(x => x.ColumnName == "Name");
    }

    [Test]
    public async Task SyncAsync_ReAllowedTable_IsUnarchivedNotDuplicated()
    {
        var store = new HostSyncStore();
        var (first, firstRegistration) = Build(store, x => x.AllowTables("Customer", "lending.Loan"));
        await first.SyncAsync(firstRegistration, CancellationToken.None);
        var (narrow, narrowRegistration) = Build(store, x => x.AllowTables("Customer"));
        await narrow.SyncAsync(narrowRegistration, CancellationToken.None);

        await first.SyncAsync(firstRegistration, CancellationToken.None);

        store.Tables.Where(x => x.TableName == "Loan").Should().ContainSingle()
            .Which.ArchivedTime.Should().BeNull();
    }

    [Test]
    public async Task SyncAsync_MissingReadOnlyConnectionString_ThrowsWithoutPersisting()
    {
        var store = new HostSyncStore();
        var (synchronizer, registration) = Build(store, x => x.AllowTables("Customer"), connectionString: null);

        var act = () => synchronizer.SyncAsync(registration, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ConnectionStrings:BeaconReadOnly*");
        store.DataSources.Should().BeEmpty();
    }

    [Test]
    public void Registration_WithoutReadOnlyConnectionStringName_Throws()
    {
        var act = () => new HostDataSourceRegistration(typeof(HostTestContext), new HostDbContextOptions());

        act.Should().Throw<InvalidOperationException>().WithMessage("*ReadOnlyConnectionStringName*");
    }

    [Test]
    public void ExposeDbContext_DuplicateName_Throws()
    {
        var builder = new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build());
        builder.ExposeDbContext<HostTestContext>(x => x.ReadOnlyConnection("A").AllowTables("Customer"));

        var act = () => builder.ExposeDbContext<HostTestContext>(x => x.ReadOnlyConnection("B"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*HostTestContext*already registered*");
    }

    [Test]
    public void ConnectionResolver_HostManagedSource_ResolvesFromConfigurationAtCallTime()
    {
        var resolver = new DataSourceConnectionResolver(FakeEncryption.Instance, Configuration(ReadOnlyConnectionString), TestTransport.Policy(), new BeaconConfiguration(), TestTransport.Hasher());

        var connection = resolver.GetConnectionString(HostDataSource());

        connection.Should().Be(ReadOnlyConnectionString);
    }

    [Test]
    public void ConnectionResolver_HostManagedSourceWithoutConfiguredString_Throws()
    {
        var resolver = new DataSourceConnectionResolver(FakeEncryption.Instance, Configuration(null), TestTransport.Policy(), new BeaconConfiguration(), TestTransport.Hasher());

        var act = () => resolver.GetConnectionString(HostDataSource());

        act.Should().Throw<InvalidOperationException>().WithMessage("*BeaconReadOnly*");
    }

    [Test]
    public void ConnectionResolver_OrdinarySource_DecryptsStoredString()
    {
        var resolver = new DataSourceConnectionResolver(FakeEncryption.Instance, Configuration(ReadOnlyConnectionString), TestTransport.Policy(), new BeaconConfiguration(), TestTransport.Hasher());
        var dataSource = new DataSource
        {
            Id = 7,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = FakeEncryption.Instance.Encrypt("Host=wh")
        };

        resolver.GetConnectionString(dataSource).Should().Be("Host=wh");
    }

    [Test]
    public void Guard_UnregisteredHostKey_RejectsEverything()
    {
        var guard = new HostDataSourceGuard(BuildRegistry(x => x.AllowTables("Customer")));

        var result = guard.Check("efcore:SomebodyElse", "SELECT Name FROM Customer");

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("not registered");
    }

    [Test]
    public void Guard_OrdinaryDataSource_IsNotChecked()
    {
        var guard = new HostDataSourceGuard(BuildRegistry(x => { }));
        var dataSource = new DataSource { Name = "wh", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "x" };

        guard.Check(dataSource, "SELECT * FROM anything").Allowed.Should().BeTrue();
    }

    [Test]
    public void Registry_ResolvesScopedContextAndKeepsDesignTimeComments()
    {
        var registry = BuildRegistry(x => x.AllowTables("Customer"));

        var snapshot = registry.GetSnapshot("efcore:Netgiro");

        snapshot.Should().NotBeNull();
        snapshot!.Tables.Single().Description.Should().Be("Customers of the bank");
    }

    [Test]
    public void Gate_HostPolicyViolation_BlocksEvenWhenSchemaIsAdvisory()
    {
        var guard = new HostDataSourceGuard(BuildRegistry(x => x.AllowTables("Customer")));
        var gate = TestSqlGate.Create(hostGuard: guard);

        var report = gate.Evaluate(SqlGateRequest.FromSettings("SELECT Payload FROM AuditLog", "MSSQL", TestSqlGate.DefaultSettings(), TestRowSecurity.NotApplicable) with
        {
            BlockOnSchemaFailure = false,
            HostManagedKey = "efcore:Netgiro"
        });

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.HostPolicy);
        report.BlockReason.Should().Contain("AuditLog");
    }

    [Test]
    public void Gate_HostMaskedColumns_AreReportedAsPii()
    {
        var guard = new HostDataSourceGuard(BuildRegistry(x => x.AllowTables("Customer").MaskColumns(y => y.Name == "Name")));
        var gate = TestSqlGate.Create(hostGuard: guard);

        var report = gate.Evaluate(SqlGateRequest.FromSettings("SELECT Name AS n FROM dbo.Customer", "MSSQL", TestSqlGate.DefaultSettings(), TestRowSecurity.NotApplicable) with
        {
            HostManagedKey = "efcore:Netgiro"
        });

        report.Blocked.Should().BeFalse(report.BlockReason);
        report.PiiColumns.Should().Contain("n", "the masked column leaves the query under its alias");
    }

    // The report names the host-masked output columns exactly as the host check derived them, for full masking.
    [Test]
    public void Gate_HostMaskedColumns_AreTheHostChecksMaskedOutputColumns()
    {
        const string sql = "SELECT Id, Name AS n FROM dbo.Customer";
        var guard = new HostDataSourceGuard(BuildRegistry(x => x.AllowTables("Customer").MaskColumns(y => y.Name == "Name")));
        var gate = TestSqlGate.Create(hostGuard: guard);

        var report = gate.Evaluate(SqlGateRequest.FromSettings(sql, "MSSQL", TestSqlGate.DefaultSettings(), TestRowSecurity.NotApplicable) with
        {
            HostManagedKey = "efcore:Netgiro"
        });

        report.Blocked.Should().BeFalse(report.BlockReason);
        report.HostMaskedColumns.Should().Equal(guard.Check("efcore:Netgiro", sql).MaskedOutputColumns);
        report.HostMaskedColumns.Should().Equal("n");
    }

    [Test]
    public void Gate_OrdinarySource_HasNoHostMaskedColumns()
    {
        var report = TestSqlGate.Create().Evaluate(SqlGateRequest.FromSettings("SELECT email FROM users", "PostgreSQL", TestSqlGate.DefaultSettings(), TestRowSecurity.NotApplicable));

        report.HostMaskedColumns.Should().BeEmpty();
    }

    [Test]
    public void Gate_HostKeyWithoutGuard_FailsClosed()
    {
        var report = TestSqlGate.Create().Evaluate(
            SqlGateRequest.FromSettings("SELECT 1", "MSSQL", TestSqlGate.DefaultSettings(), TestRowSecurity.NotApplicable) with { HostManagedKey = "efcore:Netgiro" });

        report.Blocked.Should().BeTrue();
    }

    [Test]
    public async Task DataSourceService_HostManagedSource_RefusesUpdateAndDelete()
    {
        var store = new HostSyncStore();
        store.DataSources.Add(HostDataSource());
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HostSyncTestContext(store));
        var service = new DataSourceService(
            factory.Object,
            FakeEncryption.Instance,
            Mock.Of<Beacon.Core.Services.Providers.IDataSourceProviderFactory>(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<Beacon.Core.Services.Metadata.ISchemaGraphService>(),
            new RecordingSecurityAudit(),
            TestTransport.Policy(),
            TestEngineVerifier.Passing,
            TestChangeApprovals.Submitter(),
            TestTransport.Hasher(),
            NullLogger<DataSourceService>.Instance);

        var update = () => service.UpdateDataSource(new Beacon.Core.Models.DataSources.DataSourceData
        {
            DataSourceId = 7,
            Name = "renamed",
            ConnectionString = "Server=evil"
        }, CancellationToken.None);
        var delete = () => service.DeleteDataSource(7, CancellationToken.None);

        await update.Should().ThrowAsync<InvalidOperationException>().WithMessage("*managed by the host*");
        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*managed by the host*");
        store.DataSources.Single().Name.Should().Be("Netgiro");
        store.DataSources.Single().ArchivedTime.Should().BeNull();
        store.SaveCount.Should().Be(0);
    }

    private static DataSource HostDataSource()
    {
        return new DataSource
        {
            Id = 7,
            Name = "Netgiro",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.MSSQL,
            HostManagedKey = "efcore:Netgiro",
            EncryptedConnectionData = FakeEncryption.Instance.Encrypt("{\"hostConnectionStringName\":\"BeaconReadOnly\"}")
        };
    }

    private static (HostDataSourceSynchronizer Synchronizer, HostDataSourceRegistration Registration) Build(
        HostSyncStore store,
        Action<HostDbContextOptions> configure,
        Mock<ISchemaRelationshipSyncService>? relationships = null,
        string? connectionString = ReadOnlyConnectionString,
        string projectName = "Netgiro Admin",
        IProjectMembershipSource? membershipSource = null,
        LogRecorder? logs = null,
        IHostReadOnlyLoginCheck? loginCheck = null,
        ICatalogSnapshotCache? snapshots = null)
    {
        var registry = BuildRegistry(configure, projectName);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HostSyncTestContext(store));

        var synchronizer = new HostDataSourceSynchronizer(
            factory.Object,
            new HostProjectResolver(factory.Object, NullLogger<HostProjectResolver>.Instance),
            registry,
            FakeEncryption.Instance,
            Configuration(connectionString),
            (relationships ?? new Mock<ISchemaRelationshipSyncService>()).Object,
            Mock.Of<ISchemaGraphService>(),
            new MemoryCache(new MemoryCacheOptions()),
            membershipSource ?? BeaconMembership(factory.Object),
            Options.Create(new ProjectAuthorizationOptions()),
            loginCheck ?? new RecordingHostLoginCheck(),
            logs?.For<HostDataSourceSynchronizer>() ?? NullLogger<HostDataSourceSynchronizer>.Instance,
            snapshots);

        return (synchronizer, registry.Registrations.Single());
    }

    // Beacon manages project membership (user management on, the default membership source).
    private static IProjectMembershipSource BeaconMembership(IDbContextFactory<BeaconContext> factory)
    {
        var configuration = new BeaconConfiguration();
        configuration.EnableUserManagement();

        return new DatabaseProjectMembershipSource(configuration, factory);
    }

    private static HostDataSourceRegistry BuildRegistry(Action<HostDbContextOptions> configure, string projectName = "Netgiro Admin")
    {
        var services = new ServiceCollection();
        services.AddDbContext<HostTestContext>(x => x.UseSqlServer("Server=unused;Database=unused"));
        var provider = services.BuildServiceProvider();

        var options = new HostDbContextOptions
        {
            Name = "Netgiro",
            ProjectName = projectName,
            ReadOnlyConnectionStringName = "BeaconReadOnly"
        };
        configure(options);

        return new HostDataSourceRegistry(
            [new HostDataSourceRegistration(typeof(HostTestContext), options)],
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeXmlDocumentation());
    }

    private static IConfiguration Configuration(string? connectionString)
    {
        var values = new Dictionary<string, string?>();
        if (connectionString != null)
        {
            values["ConnectionStrings:BeaconReadOnly"] = connectionString;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class FakeEncryption : IEncryptionService
    {
        public const string Prefix = "enc:";

        public static readonly FakeEncryption Instance = new();

        public string Encrypt(string plainText) => Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));

        public string Decrypt(string cipherText) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cipherText[Prefix.Length..]));
    }
}

internal sealed class RecordingHostLoginCheck : IHostReadOnlyLoginCheck
{
    public List<(int DataSourceId, IReadOnlyList<HostExposedTable> Tables)> Calls { get; } = [];

    /// <summary>Runs on each call, before it returns.</summary>
    public Action? OnCall { get; init; }

    /// <summary>What each call throws, as a check that breaks its own contract would; null returns.</summary>
    public Exception? Failure { get; init; }

    public Task WarnIfWritableAsync(DataSource dataSource, IReadOnlyList<HostExposedTable> exposedTables, CancellationToken cancellationToken)
    {
        Calls.Add((dataSource.Id, exposedTables));
        OnCall?.Invoke();

        return Failure == null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
