using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Connector.Api;
using Beacon.Connector.AzureSynapse;
using Beacon.Connector.BigQuery;
using Beacon.Connector.CloudWatch;
using Beacon.Connector.Databricks;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.Snowflake;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.DataSourceTransport;

/// <summary>
/// What Beacon does with a connection that does not authenticate the server over TLS: creating, changing or testing one
/// is refused; a stored one keeps connecting with one Warning per data source per process, or is refused under
/// <c>EnforceOnExisting</c>; <c>AllowInsecure</c> lifts both and is warned about outside Development; the data-source list
/// carries the verdict as a badge. No refusal or Warning carries a value from the connection string.
/// </summary>
[TestFixture]
public class DataSourceTransportEnforcementTests
{
    private const string Host = "leaky-host-7f3a.internal";

    private static readonly string Password = TestCredentials.ProviderSecretKey;

    private static readonly string Insecure = $"Host={Host};Database=sales;Password={Password};SSL Mode=Require";

    private static readonly string Verified = $"Host={Host};Database=sales;Password={Password};SSL Mode=VerifyFull";

    [Test]
    public async Task CreatingADataSource_WithAnInsecureConnection_IsRefused_AndNothingIsSaved()
    {
        var saved = new List<object>();
        var service = Service(saved, [], TestTransport.Policy());

        var act = () => service.CreateDataSource(Data(Insecure), CancellationToken.None);

        var refusal = (await act.Should().ThrowAsync<DataSourceTransportException>()).Which;
        refusal.Message.Should().Contain("SSL Mode=VerifyFull").And.NotContainAny(Host, Password);
        saved.Should().BeEmpty();
    }

    [Test]
    public async Task CreatingADataSource_WithAVerifiedConnection_IsSaved()
    {
        var saved = new List<object>();
        var service = Service(saved, [], TestTransport.Policy());

        await service.CreateDataSource(Data(Verified), CancellationToken.None);

        saved.OfType<DataSource>().Should().ContainSingle();
    }

    [Test]
    public async Task ChangingADataSourcesConnection_ToAnInsecureOne_IsRefused_AndNothingIsSaved()
    {
        var saved = new List<object>();
        var stored = Stored(12, Verified);
        var service = Service(saved, [stored], TestTransport.Policy());

        var act = () => service.UpdateDataSource(Data(Insecure, id: 12), CancellationToken.None);

        (await act.Should().ThrowAsync<DataSourceTransportException>()).Which.Message.Should().NotContainAny(Host, Password);
        saved.Should().BeEmpty();
        stored.EncryptedConnectionData.Should().Be(Encryption.Encrypt(Verified));
    }

    [Test]
    public async Task EditingAStoredInsecureSource_KeepingItsConnection_IsAllowed()
    {
        var saved = new List<object>();
        var stored = Stored(12, Insecure);
        var service = Service(saved, [stored], TestTransport.Policy());

        await service.UpdateDataSource(Data(Insecure, id: 12, name: "renamed"), CancellationToken.None);

        stored.Name.Should().Be("renamed", "a stored connection kept as it is stays a stored one");
    }

    [Test]
    public async Task ChangingTheEngineOfAStoredConnection_ChecksItAsTheNewEngine()
    {
        var stored = Stored(12, Verified);
        var service = Service([], [stored], TestTransport.Policy());

        var act = () => service.UpdateDataSource(Data(Verified, id: 12, engine: DatabaseEngineType.MSSQL), CancellationToken.None);

        (await act.Should().ThrowAsync<DataSourceTransportException>()).Which.Message.Should().Contain("connection string");
    }

    [Test]
    public async Task TestingAnInsecureConnection_IsRefused_BeforeItOpens()
    {
        var service = Service([], [], TestTransport.Policy());

        var database = await service.TestConnectionAsync(Data(Insecure), CancellationToken.None);
        var direct = await service.TestConnection(Insecure, DatabaseEngineType.PostgreSQL, CancellationToken.None);
        var api = await service.TestConnectionAsync(
            new DataSourceData { Name = "api", DataSourceType = DataSourceType.Api, ConnectionString = $"{{\"baseUrl\":\"http://{Host}\",\"openApiSpecUrl\":\"https://{Host}/spec\",\"auth\":{{\"token\":\"{Password}\"}}}}" },
            CancellationToken.None);

        foreach (var response in new[] { database, direct, api })
        {
            response.Success.Should().BeFalse();
            response.Message.Should().StartWith("Connection refused:").And.NotContainAny(Host, Password);
        }

        api.Message.Should().Contain("BaseUrl");
    }

    [Test]
    public async Task AllowInsecure_LiftsTheCheck_OnCreate()
    {
        var saved = new List<object>();
        var service = Service(saved, [], TestTransport.Policy(allowInsecure: true));

        await service.CreateDataSource(Data(Insecure), CancellationToken.None);

        saved.OfType<DataSource>().Should().ContainSingle();
    }

    [Test]
    public void AStoredInsecureSource_StillConnects_WithOneWarningPerSourcePerProcess_IdsOnly()
    {
        var log = new LogRecorder();
        var resolver = Resolver(TestTransport.Policy(logger: log.For<DataSourceTransportPolicy>()));

        var first = resolver.GetConnectionString(Stored(5, Insecure));
        resolver.GetConnectionString(Stored(5, Insecure));
        resolver.GetConnectionString(Stored(6, Insecure));
        resolver.GetConnectionString(Stored(7, Verified));

        first.Should().Be(Insecure, "a stored source keeps working until its connection is replaced");
        var warnings = log.Entries
            .Where(x => x.Level == LogLevel.Warning)
            .ToList();
        warnings.Select(x => x.Message).Should().SatisfyRespectively(
            x => x.Should().Contain("Data source 5").And.Contain("SSL Mode"),
            x => x.Should().Contain("Data source 6"));
        warnings.Should().OnlyContain(x => !x.FullText.Contains(Host) && !x.FullText.Contains(Password));
    }

    [Test]
    public void EnforceOnExisting_RefusesAStoredInsecureSource_WhenItConnects()
    {
        var resolver = Resolver(TestTransport.Policy(enforceOnExisting: true));

        var insecure = () => resolver.GetConnectionString(Stored(5, Insecure));

        insecure.Should().Throw<DataSourceTransportException>().Which.Message.Should().Contain("SSL Mode").And.NotContainAny(Host, Password);
        resolver.GetConnectionString(Stored(7, Verified)).Should().Be(Verified);
    }

    [Test]
    public void AllowInsecure_LiftsTheCheck_OnStoredSources_WithoutAWarning()
    {
        var log = new LogRecorder();
        var resolver = Resolver(TestTransport.Policy(allowInsecure: true, enforceOnExisting: true, logger: log.For<DataSourceTransportPolicy>()));

        resolver.GetConnectionString(Stored(5, Insecure)).Should().Be(Insecure);
        log.Entries.Should().BeEmpty();
    }

    [Test]
    public void AHostManagedSource_FollowsTheSameRules_WithTheStringTheHostConfigures()
    {
        var log = new LogRecorder();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:BeaconReadOnly"] = Insecure })
            .Build();
        var hostSource = Stored(9, new HostConnectionReference("BeaconReadOnly").ToJson());
        hostSource.HostManagedKey = "host-context";

        var warned = new DataSourceConnectionResolver(Encryption, configuration, TestTransport.Policy(logger: log.For<DataSourceTransportPolicy>()));
        var enforced = new DataSourceConnectionResolver(Encryption, configuration, TestTransport.Policy(enforceOnExisting: true));

        warned.GetConnectionString(hostSource).Should().Be(Insecure);
        log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Which.Message.Should().Contain("Data source 9");
        warned.InspectTransport(hostSource).Verified.Should().BeFalse();
        var act = () => enforced.GetConnectionString(hostSource);
        act.Should().Throw<DataSourceTransportException>();
    }

    [Test]
    public void AnUnsavedSource_ThroughTheResolver_IsANewConnection()
    {
        var resolver = Resolver(TestTransport.Policy());

        var act = () => resolver.GetConnectionString(Stored(0, Insecure));

        act.Should().Throw<DataSourceTransportException>();
    }

    [Test]
    public void ARefusedStoredSource_StaysARefusal_NeverAnUndeterminedDatabase()
    {
        var configured = new ConfiguredDatabaseResolver(Resolver(TestTransport.Policy(enforceOnExisting: true)), NullLogger<ConfiguredDatabaseResolver>.Instance);

        var act = () => configured.Resolve(Stored(5, Insecure));

        act.Should().Throw<DataSourceTransportException>();
        configured.Resolve(Stored(7, Verified)).Should().Be("sales");
    }

    [TestCase("Production", true, true)]
    [TestCase("Staging", true, true)]
    [TestCase("Development", true, false)]
    [TestCase("Production", false, false)]
    public void AllowInsecure_IsWarnedAboutAtStartup_OutsideDevelopmentOnly(string environment, bool allowInsecure, bool warns)
    {
        var log = new LogRecorder();
        var startup = new DataSourceTransportStartupWarning(
            Options.Create(new DataSourceTransportOptions { AllowInsecure = allowInsecure }),
            Mock.Of<IHostEnvironment>(x => x.EnvironmentName == environment),
            log.For<DataSourceTransportStartupWarning>());

        startup.Configure(_ => { })(Mock.Of<IApplicationBuilder>());

        log.Entries.Where(x => x.Level == LogLevel.Warning).Should().HaveCount(warns ? 1 : 0);
        if (warns)
        {
            log.Entries.Single().Message.Should().Contain("Beacon:DataSources:Transport:AllowInsecure");
        }
    }

    [Test]
    public async Task TheBadge_SaysWhetherEachSourcesConnectionIsVerified_AndUnknownWhenItCannotBeRead()
    {
        var log = new LogRecorder();
        var unreadable = Stored(3, Verified);
        unreadable.EncryptedConnectionData = "not encrypted by this key";
        var writeTarget = Stored(5, Verified);
        writeTarget.AllowMigrationWrites = true;
        var sources = new List<DataSource> { Stored(1, Verified), Stored(2, Insecure), unreadable, Stored(4, Insecure), writeTarget };
        var status = new DataSourceTransportStatus(
            Factory(sources, []),
            Resolver(TestTransport.Policy(enforceOnExisting: true)),
            log.For<DataSourceTransportStatus>());

        var verified = await status.VerifiedAsync([1, 2, 3, 5], CancellationToken.None);

        verified.Should().BeEquivalentTo(new Dictionary<int, bool?> { [1] = true, [2] = false, [3] = null, [5] = null });
        (await status.VerifiedAsync([], CancellationToken.None)).Should().BeEmpty();
        log.Entries.Should().ContainSingle("a write target is skipped without reading its connection data, and only the unreadable source is logged")
            .Which.Message.Should().Contain("data source 3").And.Contain(nameof(InvalidOperationException)).And.NotContain("not encrypted by this key");
    }

    [Test]
    public void EnforceOnExisting_LogsEachRefusedSourceOnce_ByIdOnly_AndRefusesEveryTime()
    {
        var log = new LogRecorder();
        var resolver = Resolver(TestTransport.Policy(enforceOnExisting: true, logger: log.For<DataSourceTransportPolicy>()));

        var refusals = new[] { 5, 5, 6 }
            .Select(x => (Action)(() => resolver.GetConnectionString(Stored(x, Insecure))))
            .ToList();

        foreach (var refusal in refusals)
        {
            refusal.Should().Throw<DataSourceTransportException>();
        }

        log.Entries.Select(x => x.Message).Should().SatisfyRespectively(
            x => x.Should().Contain("Data source 5").And.Contain("refused").And.Contain("SSL Mode"),
            x => x.Should().Contain("Data source 6"));
        log.Entries.Should().OnlyContain(x => x.Level == LogLevel.Warning && !x.FullText.Contains(Host) && !x.FullText.Contains(Password));
    }

    [Test]
    public void AStoredSourceWhoseConnectionDataCannotBeRead_IsWarnedAbout_AsUnreadable()
    {
        var log = new LogRecorder();
        var resolver = Resolver(TestTransport.Policy(logger: log.For<DataSourceTransportPolicy>()));
        var garbled = $"Host={Host};NoSuchOption={Password}";

        resolver.GetConnectionString(Stored(5, garbled)).Should().Be(garbled, "a stored source keeps working until its connection is replaced");

        var warning = log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain("Data source 5").And.Contain("could not be read");
        log.Entries.Should().OnlyContain(x => !x.FullText.Contains(Host) && !x.FullText.Contains(Password) && !x.FullText.Contains("NoSuchOption"));
        log.Entries.Should().Contain(x => x.Level == LogLevel.Debug && x.Message.Contains(nameof(ArgumentException)), "the parser's refusal is logged by its type");
    }

    [Test]
    public void AnInspectorThatFailsUnexpectedly_IsLoggedAsAnError_AndNeverVerifies()
    {
        var log = new LogRecorder();
        var policy = new DataSourceTransportPolicy(
            [new ThrowingInspector(new NullReferenceException(Password))],
            Options.Create(new DataSourceTransportOptions()),
            log.For<DataSourceTransportPolicy>());

        var verdict = policy.Inspect(DataSourceType.Database, DatabaseEngineType.PostgreSQL, Verified);

        verdict.Should().BeEquivalentTo(new { Verified = false, Unreadable = true, Option = "connection string" }, x => x.ExcludingMissingMembers());
        var error = log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Error).Which;
        error.Message.Should().Contain(nameof(NullReferenceException)).And.Contain("PostgreSQL");
        error.FullText.Should().NotContain(Password);
    }

    [Test]
    public void TheLastInspectorRegisteredForAType_IsTheOneThatDecides()
    {
        var hostOverride = new DataSourceTransportPolicy(
            [new PostgreSqlTransportInspector(), new ThrowingInspector(null)],
            Options.Create(new DataSourceTransportOptions()),
            NullLogger<DataSourceTransportPolicy>.Instance);

        hostOverride.Inspect(DataSourceType.Database, DatabaseEngineType.PostgreSQL, Insecure).Verified
            .Should().BeTrue("a host that registers its own inspector after the connector's replaces it");
    }

    [Test]
    public async Task ChangingTheEngineOfAStoredConnection_ToSqlServer_ChecksItsEncryptOption()
    {
        var sqlServerShaped = $"Server={Host};Database=sales;Password={Password};Encrypt=False";
        var stored = Stored(12, sqlServerShaped);
        var service = Service([], [stored], TestTransport.Policy());

        var act = () => service.UpdateDataSource(Data(sqlServerShaped, id: 12, engine: DatabaseEngineType.MSSQL), CancellationToken.None);

        (await act.Should().ThrowAsync<DataSourceTransportException>()).Which.Message.Should().Contain("Encrypt=Mandatory").And.NotContainAny(Host, Password);
    }

    [Test]
    public void TheOptions_AreBoundFromConfiguration_ByTheBeaconRegistration()
    {
        using var enforcing = Registered(new Dictionary<string, string?> { ["Beacon:DataSources:Transport:EnforceOnExisting"] = "true" });
        using var lenient = Registered(new Dictionary<string, string?>());

        var refused = () => enforcing.GetRequiredService<IDataSourceTransportPolicy>().EnsureStoredConnectionAllowed(Stored(5, Insecure), Insecure);
        var warned = () => lenient.GetRequiredService<IDataSourceTransportPolicy>().EnsureStoredConnectionAllowed(Stored(5, Insecure), Insecure);

        refused.Should().Throw<DataSourceTransportException>();
        warned.Should().NotThrow();
    }

    [Test]
    public void TheStartupWarning_IsRegisteredByTheApi_AndReadsTheBoundOptions()
    {
        var log = new LogRecorder();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerProvider>(log);
        services.AddSingleton(Mock.Of<IHostEnvironment>(x => x.EnvironmentName == "Production"));
        services.AddBeaconApiAuthorization();
        using var provider = Registered(new Dictionary<string, string?> { ["Beacon:DataSources:Transport:AllowInsecure"] = "true" }, services);

        var descriptor = services.Should().ContainSingle(x => x.ServiceType == typeof(IStartupFilter) && x.ImplementationType == typeof(DataSourceTransportStartupWarning)).Which;
        var filter = (IStartupFilter)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
        filter.Configure(_ => { })(Mock.Of<IApplicationBuilder>());

        log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Which.Message.Should().Contain("Beacon:DataSources:Transport:AllowInsecure");
    }

    [Test]
    public void EachConnector_RegistersItsInspector_AndAHostWithoutOne_IsNeverVerified()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        new BeaconBuilder(services, configuration)
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddAzureSynapseConnector()
            .AddMySqlConnector()
            .AddSnowflakeConnector()
            .AddApiConnector()
            .AddDatabricksConnector()
            .AddBigQueryConnector()
            .AddCloudWatchConnector()
            .AddPostgreSqlConnector();
        services.AddLogging();
        services.AddOptions<DataSourceTransportOptions>();
        services.AddSingleton<IDataSourceTransportPolicy, DataSourceTransportPolicy>();
        using var provider = services.BuildServiceProvider();

        var registered = provider.GetServices<IDataSourceTransportInspector>()
            .Select(x => (x.DataSourceType, x.DatabaseEngineType))
            .ToList();
        var policy = provider.GetRequiredService<IDataSourceTransportPolicy>();

        registered.Should().OnlyHaveUniqueItems("a connector added twice registers its inspector once");
        registered.Should().BeEquivalentTo(TestTransport.Inspectors().Select(x => (x.DataSourceType, x.DatabaseEngineType)));
        policy.Inspect(DataSourceType.Database, DatabaseEngineType.PostgreSQL, Verified).Verified.Should().BeTrue();
        new DataSourceTransportPolicy([], Options.Create(new DataSourceTransportOptions()), NullLogger<DataSourceTransportPolicy>.Instance)
            .Inspect(DataSourceType.Database, DatabaseEngineType.PostgreSQL, Verified)
            .Should().BeEquivalentTo(new { Verified = false, Option = "data source type" }, x => x.ExcludingMissingMembers());
    }

    private static readonly PrefixEncryption Encryption = new();

    // The Beacon registration a host makes (AddBeaconServices), with the given configuration and a throwaway key.
    private static ServiceProvider Registered(Dictionary<string, string?> settings, ServiceCollection? services = null)
    {
        services ??= new ServiceCollection();
        settings["Beacon:EncryptionKey"] = TestEncryptionKey.Value;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        services.AddLogging();
        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());

        return services.BuildServiceProvider();
    }

    private static DataSourceData Data(string connectionString, int id = 0, string name = "warehouse", DatabaseEngineType engine = DatabaseEngineType.PostgreSQL)
    {
        return new DataSourceData
        {
            DataSourceId = id,
            Name = name,
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = engine,
            ConnectionString = connectionString
        };
    }

    private static DataSource Stored(int id, string connectionString)
    {
        return new DataSource
        {
            Id = id,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = Encryption.Encrypt(connectionString)
        };
    }

    private static DataSourceConnectionResolver Resolver(IDataSourceTransportPolicy policy)
    {
        return new DataSourceConnectionResolver(Encryption, new ConfigurationBuilder().Build(), policy);
    }

    private static IDbContextFactory<BeaconContext> Factory(List<DataSource> sources, List<object> saved)
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet(sources, saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        return factory.Object;
    }

    private static DataSourceService Service(List<object> saved, List<DataSource> sources, IDataSourceTransportPolicy policy)
    {
        return new DataSourceService(
            Factory(sources, saved),
            Encryption,
            Mock.Of<IDataSourceProviderFactory>(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            new RecordingSecurityAudit(),
            policy,
            NullLogger<DataSourceService>.Instance);
    }

    private sealed class ThrowingInspector(Exception? exception) : IDataSourceTransportInspector
    {
        public DataSourceType DataSourceType => DataSourceType.Database;

        public DatabaseEngineType? DatabaseEngineType => Core.Data.Enums.DatabaseEngineType.PostgreSQL;

        // With no exception it verifies everything: a host's own inspector.
        public DataSourceTransportVerdict Inspect(string connectionData) =>
            exception == null ? DataSourceTransportVerdict.Pass() : throw exception;
    }

    private sealed class NoOpScheduler : Core.Worker.IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }

    private sealed class PrefixEncryption : IEncryptionService
    {
        private const string Prefix = "enc:";

        public string Encrypt(string plainText) => Prefix + plainText;

        public string Decrypt(string cipherText) => cipherText.StartsWith(Prefix, StringComparison.Ordinal)
            ? cipherText[Prefix.Length..]
            : throw new InvalidOperationException("Not encrypted with this key.");
    }
}
