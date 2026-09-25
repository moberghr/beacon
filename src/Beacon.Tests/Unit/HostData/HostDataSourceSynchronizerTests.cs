using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
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
        store.SaveCount.Should().Be(1);
        relationships.Verify(x => x.SyncAsync(dataSource.Id, It.IsAny<CancellationToken>()), Times.Once);
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
        var resolver = new DataSourceConnectionResolver(FakeEncryption.Instance, Configuration(ReadOnlyConnectionString));

        var connection = resolver.GetConnectionString(HostDataSource());

        connection.Should().Be(ReadOnlyConnectionString);
    }

    [Test]
    public void ConnectionResolver_HostManagedSourceWithoutConfiguredString_Throws()
    {
        var resolver = new DataSourceConnectionResolver(FakeEncryption.Instance, Configuration(null));

        var act = () => resolver.GetConnectionString(HostDataSource());

        act.Should().Throw<InvalidOperationException>().WithMessage("*BeaconReadOnly*");
    }

    [Test]
    public void ConnectionResolver_OrdinarySource_DecryptsStoredString()
    {
        var resolver = new DataSourceConnectionResolver(FakeEncryption.Instance, Configuration(ReadOnlyConnectionString));
        var dataSource = new DataSource
        {
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = FakeEncryption.Instance.Encrypt("Host=wh")
        };

        resolver.GetConnectionString(dataSource).Should().Be("Host=wh");
    }

    [Test]
    public void Guard_UnregisteredHostKey_RejectsEverything()
    {
        var guard = new HostDataSourceGuard(BuildRegistry(x => x.AllowTables("Customer")), Mock.Of<IQueryGuardrailService>());

        var result = guard.Check("efcore:SomebodyElse", "SELECT Name FROM Customer");

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("not registered");
    }

    [Test]
    public void Guard_OrdinaryDataSource_IsNotChecked()
    {
        var guard = new HostDataSourceGuard(BuildRegistry(x => { }), Mock.Of<IQueryGuardrailService>());
        var dataSource = new DataSource { Name = "wh", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "x" };

        guard.Check(dataSource, "SELECT * FROM anything").Allowed.Should().BeTrue();
    }

    [Test]
    public void Guard_Mask_UsesThePiiGuardrailMasking()
    {
        var guard = new HostDataSourceGuard(BuildRegistry(x => { }), new QueryGuardrailService());
        var rows = new List<Dictionary<string, object?>> { new() { ["SSN"] = "0101302989", ["Name"] = "Anna" } };

        var masked = guard.Mask(rows, ["ssn"]);

        masked[0]["SSN"].Should().Be("0***9");
        masked[0]["Name"].Should().Be("Anna");
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
        var guard = new HostDataSourceGuard(BuildRegistry(x => x.AllowTables("Customer")), new QueryGuardrailService());
        var gate = TestSqlGate.Create(hostGuard: guard);

        var report = gate.Evaluate(SqlGateRequest.FromSettings("SELECT Payload FROM AuditLog", "MSSQL", TestSqlGate.DefaultSettings()) with
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
        var guard = new HostDataSourceGuard(
            BuildRegistry(x => x.AllowTables("Customer").MaskColumns(y => y.Name == "Name")),
            new QueryGuardrailService());
        var gate = TestSqlGate.Create(hostGuard: guard);

        var report = gate.Evaluate(SqlGateRequest.FromSettings("SELECT Name AS n FROM Customer", "MSSQL", TestSqlGate.DefaultSettings()) with
        {
            HostManagedKey = "efcore:Netgiro"
        });

        report.Blocked.Should().BeFalse();
        report.PiiColumns.Should().Contain(["Name", "n"]);
    }

    [Test]
    public void Gate_HostKeyWithoutGuard_FailsClosed()
    {
        var report = TestSqlGate.Create().Evaluate(
            SqlGateRequest.FromSettings("SELECT 1", "MSSQL", TestSqlGate.DefaultSettings()) with { HostManagedKey = "efcore:Netgiro" });

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
            Mock.Of<IManualQueryExecutionLogger>(),
            Mock.Of<Beacon.Core.Authorization.IBeaconUserContext>(),
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
        string? connectionString = ReadOnlyConnectionString)
    {
        var registry = BuildRegistry(configure);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HostSyncTestContext(store));

        var synchronizer = new HostDataSourceSynchronizer(
            factory.Object,
            registry,
            FakeEncryption.Instance,
            Configuration(connectionString),
            (relationships ?? new Mock<ISchemaRelationshipSyncService>()).Object,
            Mock.Of<ISchemaGraphService>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<HostDataSourceSynchronizer>.Instance);

        return (synchronizer, registry.Registrations.Single());
    }

    private static HostDataSourceRegistry BuildRegistry(Action<HostDbContextOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddDbContext<HostTestContext>(x => x.UseSqlServer("Server=unused;Database=unused"));
        var provider = services.BuildServiceProvider();

        var options = new HostDbContextOptions
        {
            Name = "Netgiro",
            ProjectName = "Netgiro Admin",
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
