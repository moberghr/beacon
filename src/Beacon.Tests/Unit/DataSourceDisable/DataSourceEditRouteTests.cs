using System.Text.Json;
using Beacon.Api.Endpoints;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Api.Middleware;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.DataSourceDisable;

/// <summary>
/// The edit and enable routes' handlers hand every field to the data-source service as sent (a metadata option left out
/// stays null, so the stored value is kept) and the caller's user id as the actor; the refusals they raise answer the
/// status the documentation names; and a MySQL connection's pools are emptied together, the read-only one included.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class DataSourceEditRouteTests
{
    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddPostgreSqlConnector()
            .AddSqlServerConnector()
            .AddMySqlConnector();
    }

    [Test]
    public async Task TheEditHandler_HandsEveryFieldToTheService()
    {
        var source = StoredSource();
        var service = Service([source], storedConnection: "Host=old.internal;Database=sales;Password=old-pass;SSL Mode=VerifyFull");

        await new UpdateDataSourceHandler(service).Handle(
            new UpdateDataSourceCommand(12, "warehouse", DatabaseEngineType.MySQL, "Server=db;Database=sales;Uid=reader;Password=new-pass;SslMode=VerifyFull", false, 5, 6, true, ["audit"], ["sales"], ConfirmTargetChange: true),
            CancellationToken.None);

        source.Should().BeEquivalentTo(
            new
            {
                Name = "warehouse",
                DatabaseEngineType = DatabaseEngineType.MySQL,
                EncryptedConnectionData = "enc:Server=db;Database=sales;Uid=reader;Password=new-pass;SslMode=VerifyFull",
                MetadataLoadingEnabled = false,
                MetadataMaxTables = 5,
                MetadataMaxColumnsPerTable = 6,
                MetadataLoadTableNamesOnly = true,
                MetadataExcludeSchemas = "audit",
                MetadataIncludeSchemas = "sales"
            },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task TheEditHandler_LeavesOptionsLeftOutNull_SoTheStoredValuesAreKept()
    {
        var source = StoredSource();
        var service = Service([source], storedConnection: "Host=old.internal;Database=sales;Password=old-pass;SSL Mode=VerifyFull");

        await new UpdateDataSourceHandler(service).Handle(
            new UpdateDataSourceCommand(12, "warehouse", DatabaseEngineType.PostgreSQL, null, null, null, null, null, null, null),
            CancellationToken.None);

        source.Should().BeEquivalentTo(
            new
            {
                EncryptedConnectionData = "enc:Host=old.internal;Database=sales;Password=old-pass;SSL Mode=VerifyFull",
                MetadataLoadingEnabled = true,
                MetadataMaxTables = 50,
                MetadataMaxColumnsPerTable = 60,
                MetadataLoadTableNamesOnly = true,
                MetadataExcludeSchemas = "audit",
                MetadataIncludeSchemas = "sales"
            },
            x => x.ExcludingMissingMembers());
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task TheEditHandler_RefusesABlankName_BeforeTheService(string name)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        var service = Service([StoredSource()], factory: factory.Object);

        var act = () => new UpdateDataSourceHandler(service).Handle(
            new UpdateDataSourceCommand(12, name, DatabaseEngineType.PostgreSQL, null, null, null, null, null, null, null),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a name*");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task TheEnableHandler_HandsTheStateAndTheCallersUserIdToTheService(bool enabled)
    {
        var source = StoredSource();
        source.DisabledTime = enabled ? DateTime.UtcNow : null;
        source.DisabledByUserId = enabled ? 3 : null;
        var service = Service([source], storedConnection: "Host=old.internal;Database=sales;Password=old-pass;SSL Mode=VerifyFull");
        var actors = new Mock<IBeaconActorAccessor>();
        actors.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new BeaconActor("ext-ada", false) { BeaconUserId = 7 });

        await new SetDataSourceEnabledHandler(service, actors.Object).Handle(new SetDataSourceEnabledCommand(12, enabled), CancellationToken.None);

        source.DisabledTime.HasValue.Should().Be(!enabled);
        source.DisabledByUserId.Should().Be(enabled ? null : 7);
    }

    // A disabled source is a 400 carrying its generic message (the host's exception middleware maps every BeaconException).
    [Test]
    public async Task ADisabledSource_Answers400_WithTheGenericMessage()
    {
        var middleware = new ApiExceptionMiddleware(_ => throw new DataSourceDisabledException(12), NullLogger<ApiExceptionMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/beacon/api/data-sources/12/query";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(context.Response.Body);
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.RootElement.GetProperty("title").GetString().Should().Be(DataSourceDisabledException.RefusalMessage);
    }

    // An unconfirmed change of target is a 409 carrying its message, mapped by the API group itself.
    [Test]
    public async Task AnUnconfirmedTargetChange_Answers409_WithItsMessage()
    {
        var filter = new BeaconAccessExceptionEndpointFilter(NullLogger<BeaconAccessExceptionEndpointFilter>.Instance);
        var invocation = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());

        var result = await filter.InvokeAsync(invocation, _ => throw new BeaconConflictException(DataSourceService.TargetChangeUnconfirmedMessage));

        var problem = result.Should().BeOfType<ProblemHttpResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.ProblemDetails.Title.Should().Be(DataSourceService.TargetChangeUnconfirmedMessage);
    }

    [Test]
    public void AMySqlConnectionsPools_AreEmptiedTogether_TheReadOnlyOneIncluded()
    {
        const string connection = "Server=db-a.internal;Database=sales;Uid=reader;Pwd=old-pass";
        var cleared = new List<string>();
        DbConnectionFactory.Register(DatabaseEngineType.MySQL, cs => new RecordingDbConnection { ConnectionString = cs }, x => cleared.Add(x.ConnectionString));

        DbConnectionFactory.ClearPool(DatabaseEngineType.MySQL, connection).Should().BeTrue();

        cleared.Should().Equal(connection, DbConnectionFactory.WithoutStatementBatches(connection));
        cleared[1].Should().Contain("AllowBatch=false");
    }

    [Test]
    public void APoolThatFailsToClear_DoesNotKeepTheOtherFromBeingCleared()
    {
        const string connection = "Server=db-a.internal;Database=sales;Uid=reader;Pwd=old-pass";
        var cleared = new List<string>();
        DbConnectionFactory.Register(
            DatabaseEngineType.MySQL,
            cs => new RecordingDbConnection { ConnectionString = cs },
            x =>
            {
                if (x.ConnectionString == connection)
                {
                    throw new InvalidOperationException("first pool");
                }

                cleared.Add(x.ConnectionString);
            });

        var act = () => DbConnectionFactory.ClearPool(DatabaseEngineType.MySQL, connection);

        act.Should().Throw<InvalidOperationException>().WithMessage("first pool");
        cleared.Should().Equal(DbConnectionFactory.WithoutStatementBatches(connection));
    }

    [TestCase(DatabaseEngineType.PostgreSQL)]
    [TestCase(DatabaseEngineType.MSSQL)]
    public void AnotherEngine_HasOnePool(DatabaseEngineType engine)
    {
        DbConnectionFactory.PooledConnectionStrings(engine, "Host=a;Database=b").Should().Equal("Host=a;Database=b");
    }

    private static DataSource StoredSource()
    {
        return new DataSource
        {
            Id = 12,
            Name = "stored",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "stored",
            MetadataLoadingEnabled = true,
            MetadataMaxTables = 50,
            MetadataMaxColumnsPerTable = 60,
            MetadataLoadTableNamesOnly = true,
            MetadataExcludeSchemas = "audit",
            MetadataIncludeSchemas = "sales"
        };
    }

    private static DataSourceService Service(List<DataSource> sources, string? storedConnection = null, IDbContextFactory<BeaconContext>? factory = null)
    {
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet(sources, saved),
            [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet(new List<ColumnMetadata>(), saved),
            [typeof(MigrationJob)] = RecordingBeaconContext.MemorySet(new List<MigrationJob>(), saved)
        };
        var contexts = new Mock<IDbContextFactory<BeaconContext>>();
        contexts
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns((string x) => $"enc:{x}");
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns(storedConnection!);

        return new DataSourceService(
            factory ?? contexts.Object,
            encryption.Object,
            TestProviderFactory.Accepting(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            new RecordingSecurityAudit(),
            TestTransport.Policy(),
            TestEngineVerifier.Passing,
            TestChangeApprovals.Submitter(),
            TestTransport.Hasher(),
            NullLogger<DataSourceService>.Instance);
    }
}
