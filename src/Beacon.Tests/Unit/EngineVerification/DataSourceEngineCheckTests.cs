using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.EngineVerification;

/// <summary>
/// The declared engine is checked against the server when a database data source is created, when its connection or
/// engine changes and when a connection is tested: a clear mismatch is refused and nothing is saved; a server whose answer
/// cannot be read is saved as before; an edit that keeps the stored connection connects to nothing.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class DataSourceEngineCheckTests
{
    private const string Connection = "Host=warehouse.internal;Database=sales;SSL Mode=VerifyFull";

    private const string OtherConnection = "Host=warehouse-2.internal;Database=sales;SSL Mode=VerifyFull";

    private const string RotatedConnection = "Host=warehouse.internal;Database=sales;Password=rotated;SSL Mode=VerifyFull";

    private static readonly PrefixEncryption Encryption = new();

    private int _connectionsOpened;

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    [Test]
    public async Task Creating_OnAServerOfAnotherEngine_IsRefused_AndNothingIsSaved()
    {
        Serve(new RecordingDbConnection { Scalar = _ => "CockroachDB CCL v23.1.11" });
        var saved = new List<object>();

        var act = () => Service(saved, []).CreateDataSource(Data(Connection), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("CockroachDB").And.Contain("PostgreSQL").And.NotContain("warehouse").And.NotContain("v23");
        saved.Should().BeEmpty();
    }

    [Test]
    public async Task Creating_WhenTheServerAnswerCannotBeRead_IsSaved()
    {
        Serve(new RecordingDbConnection { FailOnCommandText = _ => true });
        var saved = new List<object>();

        await Service(saved, []).CreateDataSource(Data(Connection), CancellationToken.None);

        saved.OfType<DataSource>().Should().ContainSingle();
        _connectionsOpened.Should().Be(1);
    }

    [Test]
    public async Task Creating_OnTheDeclaredEngine_IsSaved()
    {
        Serve(new RecordingDbConnection { Scalar = _ => "PostgreSQL 16.2 on x86_64-pc-linux-gnu" });
        var saved = new List<object>();

        await Service(saved, []).CreateDataSource(Data(Connection), CancellationToken.None);

        saved.OfType<DataSource>().Should().ContainSingle();
    }

    [Test]
    public async Task ChangingTheConnection_ToAServerOfAnotherEngine_IsRefused_AndTheStoredOneKept()
    {
        Serve(new RecordingDbConnection { Scalar = _ => "PostgreSQL 8.0.2 on i686-pc-linux-gnu, Redshift 1.0.77467" });
        var stored = Stored(12, Connection);
        var saved = new List<object>();

        var act = () => Service(saved, [stored]).UpdateDataSource(Data(OtherConnection, id: 12, confirm: true), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Amazon Redshift");
        saved.Should().BeEmpty();
        stored.EncryptedConnectionData.Should().Be(Encryption.Encrypt(Connection));
    }

    [Test]
    public async Task ChangingTheEngine_ToOneTheServerIsNot_IsRefused()
    {
        Serve(new RecordingDbConnection { Scalar = _ => 6 }, DatabaseEngineType.MSSQL);
        var stored = Stored(12, Connection);
        var saved = new List<object>();

        var act = () => Service(saved, [stored]).UpdateDataSource(
            Data("Server=warehouse;Database=sales;Encrypt=Strict", id: 12, engine: DatabaseEngineType.MSSQL, confirm: true),
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Azure Synapse").And.Contain("SQL Server");
        saved.Should().BeEmpty();
        stored.DatabaseEngineType.Should().Be(DatabaseEngineType.PostgreSQL);
        stored.EncryptedConnectionData.Should().Be(Encryption.Encrypt(Connection));
    }

    [Test]
    public async Task ChangingTheConnection_WhenTheServerCannotBeReached_IsSaved()
    {
        DbConnectionFactory.Register(DatabaseEngineType.PostgreSQL, _ => throw new TimeoutException("no answer"));
        var stored = Stored(12, Connection);

        await Service([], [stored]).UpdateDataSource(Data(RotatedConnection, id: 12), CancellationToken.None);

        stored.EncryptedConnectionData.Should().Be(Encryption.Encrypt(RotatedConnection));
    }

    [Test]
    public async Task EditingWithoutChangingTheConnectionOrEngine_ConnectsToNothing()
    {
        Serve(new RecordingDbConnection { Scalar = _ => "CockroachDB CCL v23.1.11" });
        var stored = Stored(12, Connection);

        await Service([], [stored]).UpdateDataSource(Data(null, id: 12, name: "renamed"), CancellationToken.None);

        stored.Name.Should().Be("renamed");
        _connectionsOpened.Should().Be(0);
    }

    [Test]
    public async Task TestingAConnection_ToAServerOfAnotherEngine_Fails_OnTheConnectionItOpened()
    {
        Serve(new RecordingDbConnection { Scalar = _ => 11 }, DatabaseEngineType.MSSQL);

        var response = await Service([], []).TestConnection("Server=warehouse;Encrypt=Strict", DatabaseEngineType.MSSQL, CancellationToken.None);

        response.Success.Should().BeFalse();
        response.Message.Should().StartWith("Connection refused:").And.Contain("Azure Synapse").And.NotContain("warehouse");
        _connectionsOpened.Should().Be(1, "the identification runs on the connection the test opened");
    }

    [Test]
    public async Task TestingAConnection_WhenTheAnswerCannotBeRead_Succeeds()
    {
        Serve(new RecordingDbConnection { FailOnCommandText = _ => true });

        var response = await Service([], []).TestConnectionAsync(Data(Connection), CancellationToken.None);

        response.Success.Should().BeTrue();
        response.Message.Should().Be("Connection successful. The server's engine could not be verified.");
    }

    [Test]
    public async Task TestingAConnection_OnTheDeclaredEngine_SucceedsPlainly()
    {
        Serve(new RecordingDbConnection { Scalar = _ => "PostgreSQL 16.2 on x86_64-pc-linux-gnu" });

        var response = await Service([], []).TestConnectionAsync(Data(Connection), CancellationToken.None);

        response.Success.Should().BeTrue();
        response.Message.Should().Be("Connection successful");
    }

    [Test]
    public async Task TestingAConnection_ToAServerOfAnotherEngine_EmptiesThePoolOfThatConnection()
    {
        var cleared = new List<string>();
        DbConnectionFactory.Register(
            DatabaseEngineType.MSSQL,
            x => new RecordingDbConnection { Scalar = _ => 11, ConnectionString = x },
            x => cleared.Add(x.ConnectionString));

        var response = await Service([], []).TestConnection("Server=warehouse;Encrypt=Strict", DatabaseEngineType.MSSQL, CancellationToken.None);

        response.Success.Should().BeFalse();
        cleared.Should().Equal("Server=warehouse;Encrypt=Strict");
    }

    [Test]
    public async Task Creating_OnAServerOfAnotherEngine_EmptiesThePoolOfThatConnection()
    {
        var cleared = new List<string>();
        DbConnectionFactory.Register(
            DatabaseEngineType.PostgreSQL,
            x => new RecordingDbConnection { Scalar = _ => "CockroachDB CCL v23.1.11", ConnectionString = x },
            x => cleared.Add(x.ConnectionString));

        var act = () => Service([], []).CreateDataSource(Data(Connection), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        cleared.Should().Equal(Connection);
    }

    [Test]
    public async Task Creating_WithAConnectionTheTransportPolicyRefuses_ConnectsToNothing()
    {
        Serve(new RecordingDbConnection { Scalar = _ => "PostgreSQL 16.2" });
        var saved = new List<object>();

        var act = () => Service(saved, []).CreateDataSource(Data("Host=warehouse.internal;Database=sales;SSL Mode=Disable"), CancellationToken.None);

        await act.Should().ThrowAsync<DataSourceTransportException>();
        _connectionsOpened.Should().Be(0);
        saved.Should().BeEmpty();
    }

    [Test]
    public async Task ChangingTheTarget_WithoutConfirming_IsAConflict_AndConnectsToNothing()
    {
        Serve(new RecordingDbConnection { Scalar = _ => "PostgreSQL 16.2" });
        var stored = Stored(12, Connection);

        var act = () => Service([], [stored]).UpdateDataSource(Data(OtherConnection, id: 12), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>();
        _connectionsOpened.Should().Be(0);
    }

    [Test]
    public async Task ResendingTheStoredConnection_ConnectsToNothing()
    {
        Serve(new RecordingDbConnection { Scalar = _ => "CockroachDB CCL v23.1.11" });
        var stored = Stored(12, Connection);

        await Service([], [stored]).UpdateDataSource(Data(Connection, id: 12, name: "renamed"), CancellationToken.None);

        stored.Name.Should().Be("renamed");
        _connectionsOpened.Should().Be(0);
    }

    [Test]
    public async Task RotatingTheCredentialsOfADisabledSource_ConnectsToNothing()
    {
        Serve(new RecordingDbConnection { Scalar = _ => "CockroachDB CCL v23.1.11" });
        var stored = Stored(12, Connection);
        stored.DisabledTime = DateTime.UtcNow;

        await Service([], [stored]).UpdateDataSource(Data(RotatedConnection, id: 12), CancellationToken.None);

        stored.EncryptedConnectionData.Should().Be(Encryption.Encrypt(RotatedConnection));
        _connectionsOpened.Should().Be(0);
    }

    [Test]
    public async Task CreatingAndChangingANonDatabaseSource_ConnectsToNothing()
    {
        var opened = 0;
        foreach (var engine in new[] { DatabaseEngineType.PostgreSQL, DatabaseEngineType.MSSQL, DatabaseEngineType.AzureSynapse, DatabaseEngineType.MySQL })
        {
            DbConnectionFactory.Register(engine, _ =>
            {
                opened++;
                return new RecordingDbConnection();
            });
        }

        var stored = new DataSource
        {
            Id = 12,
            Name = "billing",
            DataSourceType = DataSourceType.Api,
            EncryptedConnectionData = Encryption.Encrypt(ApiConnection("v1"))
        };
        var saved = new List<object>();
        var service = Service(saved, [stored]);

        await service.CreateDataSource(ApiData(ApiConnection("v1")), CancellationToken.None);
        await service.UpdateDataSource(ApiData(ApiConnection("v2"), id: 12), CancellationToken.None);

        saved.OfType<DataSource>().Should().ContainSingle();
        stored.EncryptedConnectionData.Should().Be(Encryption.Encrypt(ApiConnection("v2")));
        opened.Should().Be(0);
    }

    [Test]
    public async Task TestingAConnection_ToACompatibleEngine_SucceedsWithANote()
    {
        Serve(new RecordingDbConnection { Scalar = _ => "10.11.6-MariaDB-0+deb12u1" }, DatabaseEngineType.MySQL);

        var response = await Service([], []).TestConnection("Server=warehouse;SslMode=VerifyFull", DatabaseEngineType.MySQL, CancellationToken.None);

        response.Success.Should().BeTrue();
        response.Message.Should().StartWith("Connection successful.").And.Contain("MariaDB").And.NotContain("10.11.6");
    }

    private void Serve(RecordingDbConnection connection, DatabaseEngineType engine = DatabaseEngineType.PostgreSQL)
    {
        _connectionsOpened = 0;
        DbConnectionFactory.Register(engine, _ =>
        {
            _connectionsOpened++;
            return connection;
        });
    }

    private static DataSourceData Data(
        string? connectionString,
        int id = 0,
        string name = "warehouse",
        DatabaseEngineType engine = DatabaseEngineType.PostgreSQL,
        bool confirm = false)
    {
        return new DataSourceData
        {
            DataSourceId = id,
            Name = name,
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = engine,
            ConnectionString = connectionString,
            ConfirmTargetChange = confirm
        };
    }

    private static string ApiConnection(string version)
    {
        return $$"""{"BaseUrl":"https://api.example.com/{{version}}","OpenApiSpecUrl":"https://api.example.com/openapi.json"}""";
    }

    private static DataSourceData ApiData(string connectionData, int id = 0)
    {
        return new DataSourceData
        {
            DataSourceId = id,
            Name = "billing",
            DataSourceType = DataSourceType.Api,
            ConnectionString = connectionData
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

    private static DataSourceService Service(List<object> saved, List<DataSource> sources)
    {
        return new DataSourceService(
            Factory(sources, saved),
            Encryption,
            Mock.Of<IDataSourceProviderFactory>(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            new RecordingSecurityAudit(),
            TestTransport.Policy(),
            new DatabaseEngineVerifier(NullLogger<DatabaseEngineVerifier>.Instance),
            TestChangeApprovals.Submitter(),
            TestTransport.Hasher(),
            NullLogger<DataSourceService>.Instance);
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
