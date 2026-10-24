using System.Security.Cryptography;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Core.Worker;
using Beacon.Core.Worker.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// While the change approval workflow covers data-source connections, the connection resolver connects a governed source
/// only with its approved definition: a held new source is refused before anything is decrypted, a source whose stored
/// definition is not the approved one once it is decrypted, and a change of secrets alone still connects. With the switch
/// off nothing changes. The refusal reaches the places that report it: the run pre-check, the job's recorded reason and its
/// 409, and the configured-database read, which keeps it as the refusal. Recording doubles, no database (§4.7).
/// </summary>
[TestFixture]
public class DataSourceAwaitingApprovalTests
{
    private const int SourceId = 12;
    private const int SubscriptionId = 21;
    private const string Approved = "Host=db-a.internal;Database=sales;Username=reader;Password=old-pass;SSL Mode=VerifyFull";
    private const string Rotated = "Host=db-a.internal;Database=sales;Username=reader;Password=new-pass;SSL Mode=VerifyFull";
    private const string ApiApproved = "{\"baseUrl\":\"https://api-a.internal/v1\",\"openApiSpecUrl\":\"https://api-a.internal/spec\",\"auth\":{\"type\":\"Bearer\",\"token\":\"old-token\"}}";

    public static IEnumerable<TestCaseData> NonSecretChanges()
    {
        yield return new TestCaseData(Approved.Replace("db-a", "db-b", StringComparison.Ordinal)).SetArgDisplayNames("host");
        yield return new TestCaseData(Approved.Replace("sales", "archive", StringComparison.Ordinal)).SetArgDisplayNames("database");
        yield return new TestCaseData(Approved.Replace("reader", "writer", StringComparison.Ordinal)).SetArgDisplayNames("login");
        yield return new TestCaseData(Approved + ";Port=6432").SetArgDisplayNames("port");
        yield return new TestCaseData(Approved + ";Options=-c search_path=other").SetArgDisplayNames("options");
        yield return new TestCaseData(Approved.Replace("VerifyFull", "Require", StringComparison.Ordinal)).SetArgDisplayNames("SSL mode");
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    public void WithTheSwitchOff_AGovernedOrHeldSource_ConnectsAsBefore(bool workflowOn, bool covers)
    {
        var configuration = Configuration(workflowOn, covers);
        var resolver = TestTransport.Resolver(Encryption(), configuration: configuration);

        resolver.GetConnectionString(Source(Approved, DataSourceApprovalDefinition.Held)).Should().Be(Approved);
        resolver.GetConnectionString(Source(Approved.Replace("sales", "archive", StringComparison.Ordinal), HashOf(Approved))).Should().Contain("archive");
    }

    [Test]
    public void ASourceNotGoverned_Connects()
    {
        var resolver = TestTransport.Resolver(Encryption(), configuration: Covered());

        resolver.GetConnectionString(Source(Approved, approvedHash: null)).Should().Be(Approved);
    }

    // Strict doubles: a decrypt or a transport check would throw a MockException, not the refusal.
    [TestCase("read")]
    [TestCase("data")]
    [TestCase("write")]
    public void AHeldSource_IsRefusedOnEveryConnectionPath_BeforeAnythingIsDecrypted(string path)
    {
        var resolver = new DataSourceConnectionResolver(
            Mock.Of<IEncryptionService>(MockBehavior.Strict),
            new ConfigurationBuilder().Build(),
            Mock.Of<IDataSourceTransportPolicy>(MockBehavior.Strict),
            Covered(),
            TestTransport.Hasher());
        var source = Source(Approved, DataSourceApprovalDefinition.Held);
        source.AllowMigrationWrites = path == "write";

        Func<string> connect = path switch
        {
            "read" => () => resolver.GetConnectionString(source),
            "data" => () => resolver.GetConnectionData(source),
            _ => () => resolver.GetWriteConnectionString(source)
        };

        connect.Should().Throw<DataSourceAwaitingApprovalException>()
            .Which.Message.Should().Be(DataSourceAwaitingApprovalException.RefusalMessage)
            .And.NotContain("warehouse");
    }

    [Test]
    public void TheApprovedDefinition_Connects_AndSoDoesAChangeOfItsSecretsAlone()
    {
        var resolver = TestTransport.Resolver(Encryption(), configuration: Covered());

        resolver.GetConnectionString(Source(Approved, HashOf(Approved))).Should().Be(Approved);
        resolver.GetConnectionString(Source(Rotated, HashOf(Approved))).Should().Be(Rotated);
    }

    [TestCaseSource(nameof(NonSecretChanges))]
    public void AChangeOfAnyOtherSetting_IsRefused_OnTheReadAndTheWritePath(string stored)
    {
        var resolver = TestTransport.Resolver(Encryption(), configuration: Covered());
        var writeTarget = Source(stored, HashOf(Approved));
        writeTarget.AllowMigrationWrites = true;

        var read = () => resolver.GetConnectionString(Source(stored, HashOf(Approved)));
        var write = () => resolver.GetWriteConnectionString(writeTarget);

        read.Should().Throw<DataSourceAwaitingApprovalException>().Which.DataSourceIds.Should().Equal([SourceId]);
        write.Should().Throw<DataSourceAwaitingApprovalException>();
    }

    [Test]
    public void AnApiSource_IsRefusedForAChangeOfItsBaseUrlPath_AndConnectsWithARotatedToken()
    {
        var resolver = TestTransport.Resolver(Encryption(), configuration: Covered());
        var approvedHash = HashOf(ApiApproved, DataSourceType.Api, null);

        var moved = () => resolver.GetConnectionData(ApiSource(ApiApproved.Replace("/v1", "/v2", StringComparison.Ordinal), approvedHash));
        var rotated = ApiApproved.Replace("old-token", "new-token", StringComparison.Ordinal);

        moved.Should().Throw<DataSourceAwaitingApprovalException>();
        resolver.GetConnectionData(ApiSource(rotated, approvedHash)).Should().Be(rotated);
    }

    [Test]
    public void AnApprovalHashedUnderAPreviousKey_StillConnects()
    {
        var previous = new ConnectionDefinitionHasher("connection-definition-test-key-previous");
        var resolver = new DataSourceConnectionResolver(
            Encryption(),
            new ConfigurationBuilder().Build(),
            TestTransport.Policy(),
            Covered(),
            new ConnectionDefinitionHasher(TestTransport.DefinitionKey, ["connection-definition-test-key-previous"]));

        resolver.GetConnectionString(Source(Approved, HashOf(Approved, hasher: previous))).Should().Be(Approved);
    }

    [Test]
    public void TheTransportBadge_IsNotGated()
    {
        var resolver = TestTransport.Resolver(Encryption(), configuration: Covered());

        var verdict = resolver.InspectTransport(Source(Approved, DataSourceApprovalDefinition.Held));

        verdict.Verified.Should().BeTrue();
    }

    [Test]
    public void ADisabledSource_IsRefusedAsDisabled_BeforeItsApproval()
    {
        var resolver = TestTransport.Resolver(Mock.Of<IEncryptionService>(MockBehavior.Strict), configuration: Covered());
        var source = Source(Approved, DataSourceApprovalDefinition.Held);
        source.DisabledTime = DateTime.UtcNow;

        var act = () => resolver.GetConnectionString(source);

        act.Should().Throw<DataSourceDisabledException>();
    }

    [Test]
    public void AHostManagedSource_IsNeverGoverned()
    {
        const string HostConnection = "Host=host-db.internal;Database=app;Username=beacon_ro;SSL Mode=VerifyFull";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:BeaconReadOnly"] = HostConnection })
            .Build();
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns(new HostConnectionReference("BeaconReadOnly").ToJson());
        var resolver = new DataSourceConnectionResolver(encryption.Object, configuration, TestTransport.Policy(), Covered(), TestTransport.Hasher());
        var source = Source(Approved, DataSourceApprovalDefinition.Held);
        source.HostManagedKey = "app";

        resolver.GetConnectionString(source).Should().Be(HostConnection);
    }

    [Test]
    public void TheDefinitionReader_HashesTheStoredDefinition_AndMatchesUnderAPreviousKey()
    {
        var previous = new ConnectionDefinitionHasher("connection-definition-test-key-previous");
        IDataSourceDefinitionReader reader = new DataSourceConnectionResolver(
            Encryption(),
            new ConfigurationBuilder().Build(),
            TestTransport.Policy(),
            new BeaconConfiguration(),
            new ConnectionDefinitionHasher(TestTransport.DefinitionKey, ["connection-definition-test-key-previous"]));
        var stored = Source(Rotated, approvedHash: null);

        reader.CurrentDefinitionHash(stored).Should().Be(HashOf(Approved), "a change of secrets alone keeps the definition");
        reader.DefinitionMatches(stored, HashOf(Approved, hasher: previous)).Should().BeTrue();
        reader.DefinitionMatches(stored, HashOf(Approved.Replace("sales", "archive", StringComparison.Ordinal))).Should().BeFalse();
        reader.DefinitionMatches(stored, DataSourceApprovalDefinition.Held).Should().BeFalse();
        reader.DefinitionMatches(stored, null).Should().BeFalse();
    }

    [Test]
    public void AStoredConnectionThatCannotBeRead_HasNoDefinition()
    {
        IDataSourceDefinitionReader reader = new DataSourceConnectionResolver(
            Encryption(),
            new ConfigurationBuilder().Build(),
            TestTransport.Policy(),
            new BeaconConfiguration(),
            TestTransport.Hasher());
        var unreadable = Source(Approved, approvedHash: null);
        unreadable.EncryptedConnectionData = "under-a-key-that-is-gone";

        reader.CurrentDefinitionHash(unreadable).Should().BeNull();
        reader.DefinitionMatches(unreadable, HashOf(Approved)).Should().BeFalse();
    }

    // A stored connection Beacon cannot read is recorded once, by the source's id and the exception's type only.
    [Test]
    public void AStoredConnectionThatCannotBeRead_IsLoggedOnce_WithoutItsValue()
    {
        var logs = new LogRecorder();
        IDataSourceDefinitionReader reader = new DataSourceConnectionResolver(
            Encryption(),
            new ConfigurationBuilder().Build(),
            TestTransport.Policy(),
            new BeaconConfiguration(),
            TestTransport.Hasher(),
            logs.For<DataSourceConnectionResolver>());
        var unreadable = Source(Approved, approvedHash: null);
        unreadable.Id = 47_011;
        unreadable.EncryptedConnectionData = "under-a-key-that-is-gone";

        reader.CurrentDefinitionHash(unreadable);
        reader.DefinitionMatches(unreadable, HashOf(Approved));

        var logged = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        logged.FullText.Should().Contain("47011").And.NotContain("under-a-key-that-is-gone").And.NotContain("db.example");
    }

    [Test]
    public void TheConfiguredDatabaseOfASourceAwaitingApproval_IsTheRefusal_NeverACrossDatabaseVerdict()
    {
        var resolver = new ConfiguredDatabaseResolver(TestTransport.Resolver(Encryption(), configuration: Covered()), NullLogger<ConfiguredDatabaseResolver>.Instance);

        var act = () => resolver.Resolve(Source(Approved, DataSourceApprovalDefinition.Held));

        act.Should().Throw<DataSourceAwaitingApprovalException>();
    }

    [Test]
    public async Task AScheduledRunOnAHeldSource_IsRecordedFailed_WithAGenericReason_AndConnectsNothing()
    {
        var store = Subscribed();

        await Jobs(store, Covered(), request: null).ExecuteQuery(SubscriptionId, CancellationToken.None);

        ShouldBeAwaitingApproval(store, [GovernanceStore.DataSourceId]);
    }

    [Test]
    public async Task ARequestedRunOnAHeldSource_IsRecordedFailed_AndAnswered409()
    {
        var store = Subscribed();

        var act = () => Jobs(store, Covered(), request: new DefaultHttpContext()).ExecuteQuery(SubscriptionId, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(NotificationFailureReasons.DataSourceAwaitingApproval);
        ShouldBeAwaitingApproval(store, [GovernanceStore.DataSourceId]);
    }

    // The workflow is on but does not cover connections: the held hash means nothing, so the run goes on to its next check
    // (strict mode refuses the query that was never reviewed), never the hold.
    [Test]
    public async Task WithConnectionsNotCovered_TheRunIsNotHeld()
    {
        var store = Subscribed();
        var configuration = Configuration(workflowOn: true, covers: false);
        configuration.ApprovalWorkflow.RequireApprovedVersionToRun = true;

        await Jobs(store, configuration, request: null).ExecuteQuery(SubscriptionId, CancellationToken.None);

        var run = store.Saved.OfType<QueryExecutionHistory>().Should().ContainSingle().Subject;
        run.Comment.Should().Be(NotificationFailureReasons.NotApproved);
    }

    // The job records the refusal with every source the run would have read, whichever source of the run was refused.
    [Test]
    public async Task ARefusalOfTheSecondSource_IsRecordedWithEverySourceTheRunWouldHaveRead()
    {
        var store = Subscribed();
        var queries = new Mock<IQueryService>();
        queries
            .Setup(x => x.ExecuteQuery(SubscriptionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataSourceAwaitingApprovalException(GovernanceStore.LedgerId, [GovernanceStore.DataSourceId, GovernanceStore.LedgerId]));

        await Jobs(store, queries.Object, request: null).ExecuteQuery(SubscriptionId, CancellationToken.None);

        ShouldBeAwaitingApproval(store, [GovernanceStore.DataSourceId, GovernanceStore.LedgerId]);
    }

    private static BeaconConfiguration Configuration(bool workflowOn, bool covers)
    {
        var configuration = new BeaconConfiguration();
        configuration.ApprovalWorkflow.Enabled = workflowOn;
        configuration.ApprovalWorkflow.CoversDataSourceConnections = covers;

        return configuration;
    }

    private static BeaconConfiguration Covered()
    {
        return Configuration(workflowOn: true, covers: true);
    }

    private static string HashOf(string connection, DataSourceType type = DataSourceType.Database, DatabaseEngineType? engine = DatabaseEngineType.PostgreSQL, ConnectionDefinitionHasher? hasher = null)
    {
        return (hasher ?? TestTransport.Hasher()).Hash(
            ConnectionDefinitionPurpose.DataSourceDefinition,
            DataSourceApprovalDefinition.Canonical(TestTransport.Policy(), type, engine, connection));
    }

    private static DataSource Source(string connection, string? approvedHash) => new()
    {
        Id = SourceId,
        Name = "warehouse",
        DataSourceType = DataSourceType.Database,
        DatabaseEngineType = DatabaseEngineType.PostgreSQL,
        EncryptedConnectionData = Encrypted(connection),
        ApprovedTargetHash = approvedHash
    };

    private static DataSource ApiSource(string connection, string? approvedHash) => new()
    {
        Id = SourceId,
        Name = "orders api",
        DataSourceType = DataSourceType.Api,
        EncryptedConnectionData = Encrypted(connection),
        ApprovedTargetHash = approvedHash
    };

    private static string Encrypted(string value) => "enc:" + value;

    private static IEncryptionService Encryption()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns<string>(x => x.StartsWith("enc:", StringComparison.Ordinal)
            ? x["enc:".Length..]
            : throw new CryptographicException("unreadable"));

        return encryption.Object;
    }

    private static GovernanceStore Subscribed()
    {
        var store = new GovernanceStore();
        store.Warehouse.ApprovedTargetHash = DataSourceApprovalDefinition.Held;
        store.Subscriptions.Add(new Subscription
        {
            Id = SubscriptionId,
            QueryId = GovernanceStore.QueryId,
            Query = store.Query,
            CronExpression = "0 * * * *",
            NotificationTrigger = NotificationTrigger.Always,
        });

        return store;
    }

    private static JobService Jobs(GovernanceStore store, BeaconConfiguration configuration, HttpContext? request)
    {
        // A strict resolver: nothing may connect.
        var queries = TestQueryServices.QueryService(
            store.Factory(),
            connections: new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict).Object,
            configuration: configuration);

        return Jobs(store, queries, request);
    }

    private static JobService Jobs(GovernanceStore store, IQueryService queries, HttpContext? request)
    {
        var http = new HttpContextAccessor { HttpContext = request };

        return new JobService(
            store.Factory(),
            queries,
            Mock.Of<INotificationService>(MockBehavior.Strict),
            Mock.Of<ITaskService>(MockBehavior.Strict),
            Mock.Of<IAnomalyDetectionService>(MockBehavior.Strict),
            Mock.Of<IDataQualityEvaluationService>(MockBehavior.Strict),
            NullLogger<JobService>.Instance,
            new RunAsScope(TestRequestPrincipal.Of(http)),
            TestChangeApprovals.Deliveries(),
            requestPrincipal: TestRequestPrincipal.Of(http));
    }

    private static void ShouldBeAwaitingApproval(GovernanceStore store, int[] dataSourceIds)
    {
        var run = store.Saved.OfType<QueryExecutionHistory>().Should().ContainSingle().Subject;
        run.NotificationStatus.Should().Be(NotificationStatus.Failed);
        run.Comment.Should().Be(NotificationFailureReasons.DataSourceAwaitingApproval);
        run.ResultCount.Should().Be(0);
        run.DataSourceIds.Should().Equal(dataSourceIds);
        NotificationFailureReasons.Displayable(run.Comment).Should().Be(NotificationFailureReasons.DataSourceAwaitingApproval, "readers of the history see the reason");
    }
}
