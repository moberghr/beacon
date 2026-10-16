using System.Text.Json;
using Beacon.Api.Endpoints;
using Beacon.Connector.MySql;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Services;
using Beacon.SampleProject.Middleware;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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
        var service = new Mock<IDataSourceService>();
        DataSourceData? sent = null;
        service
            .Setup(x => x.UpdateDataSource(It.IsAny<DataSourceData>(), It.IsAny<CancellationToken>()))
            .Callback<DataSourceData, CancellationToken>((x, _) => sent = x)
            .Returns(Task.CompletedTask);

        await new UpdateDataSourceHandler(service.Object).Handle(
            new UpdateDataSourceCommand(12, "warehouse", DatabaseEngineType.MySQL, "Server=db;Database=sales", false, 5, 6, true, ["audit"], ["sales"], ConfirmTargetChange: true),
            CancellationToken.None);

        sent.Should().BeEquivalentTo(new DataSourceData
        {
            DataSourceId = 12,
            Name = "warehouse",
            DatabaseEngineType = DatabaseEngineType.MySQL,
            ConnectionString = "Server=db;Database=sales",
            ConfirmTargetChange = true,
            MetadataLoadingEnabled = false,
            MetadataMaxTables = 5,
            MetadataMaxColumnsPerTable = 6,
            MetadataLoadTableNamesOnly = true,
            MetadataExcludeSchemas = ["audit"],
            MetadataIncludeSchemas = ["sales"]
        });
    }

    [Test]
    public async Task TheEditHandler_LeavesOptionsLeftOutNull_SoTheStoredValuesAreKept()
    {
        var service = new Mock<IDataSourceService>();
        DataSourceData? sent = null;
        service
            .Setup(x => x.UpdateDataSource(It.IsAny<DataSourceData>(), It.IsAny<CancellationToken>()))
            .Callback<DataSourceData, CancellationToken>((x, _) => sent = x)
            .Returns(Task.CompletedTask);

        await new UpdateDataSourceHandler(service.Object).Handle(
            new UpdateDataSourceCommand(12, "warehouse", DatabaseEngineType.PostgreSQL, null, null, null, null, null, null, null),
            CancellationToken.None);

        sent!.ConnectionString.Should().BeNull();
        sent.ConfirmTargetChange.Should().BeFalse();
        sent.MetadataLoadingEnabled.Should().BeNull();
        sent.MetadataMaxTables.Should().BeNull();
        sent.MetadataMaxColumnsPerTable.Should().BeNull();
        sent.MetadataLoadTableNamesOnly.Should().BeNull();
        sent.MetadataExcludeSchemas.Should().BeNull();
        sent.MetadataIncludeSchemas.Should().BeNull();
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task TheEditHandler_RefusesABlankName_BeforeTheService(string name)
    {
        var service = new Mock<IDataSourceService>(MockBehavior.Strict);

        var act = () => new UpdateDataSourceHandler(service.Object).Handle(
            new UpdateDataSourceCommand(12, name, DatabaseEngineType.PostgreSQL, null, null, null, null, null, null, null),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a name*");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task TheEnableHandler_HandsTheStateAndTheCallersUserIdToTheService(bool enabled)
    {
        var service = new Mock<IDataSourceService>();
        var actors = new Mock<IBeaconActorAccessor>();
        actors.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new BeaconActor("ext-ada", false) { BeaconUserId = 7 });

        await new SetDataSourceEnabledHandler(service.Object, actors.Object).Handle(new SetDataSourceEnabledCommand(12, enabled), CancellationToken.None);

        service.Verify(x => x.SetDataSourceEnabled(12, enabled, 7, It.IsAny<CancellationToken>()), Times.Once);
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
}
