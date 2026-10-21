using System.Net;
using System.Text;
using System.Text.Json;
using Beacon.Connector.Api;
using Beacon.Connector.Api.Services;
using Beacon.Connector.BigQuery;
using Beacon.Connector.Databricks;
using Beacon.Connector.Snowflake;
using Beacon.Core;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Google;
using Google.Apis.Bigquery.v2.Data;
using Google.Apis.Requests;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The warehouse connectors bound what one statement may cost: BigQuery bills at most a configured number of bytes and
/// cancels its job when the wait ends, Databricks cancels its statement on the caller's cancellation and limits its
/// result, Snowflake runs under a server-side statement timeout, and every stored timeout is clamped. No service or
/// database is reached: mocked BigQuery clients, scripted HTTP handlers and recording connections.
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class WarehouseCostControlTests
{
    private const string SelectSql = "SELECT id FROM orders";

    [TearDown]
    public void RestoreConnectionFactories()
    {
        new BeaconBuilder(new ServiceCollection(), new ConfigurationBuilder().Build())
            .AddSnowflakeConnector();
    }

    [TestCase(0, 300)]
    [TestCase(-5, 300)]
    [TestCase(1, 1)]
    [TestCase(600, 600)]
    [TestCase(900, 900)]
    [TestCase(5000, 900)]
    public void DataSourceQueryTimeout_ClampsAStoredValue(int stored, int expected)
    {
        DataSourceQueryTimeout.Clamp(stored).Should().Be(expected);
    }

    [Test]
    public async Task BigQuery_ASourceWithoutALimit_BillsAtMostTheDefault()
    {
        var client = RunningClient();

        await BigQuery(client.Object, """{"ProjectId":"acme-prod"}""").ExecuteQueryAsync(Source(DataSourceType.BigQuery), SelectSql, []);

        QueryJobs(client).Should().ContainSingle()
            .Which.MaximumBytesBilled.Should().Be(BigQueryConfiguration.DefaultMaximumBytesBilled);
        BigQueryConfiguration.DefaultMaximumBytesBilled.Should().Be(10L * 1024 * 1024 * 1024);
    }

    [Test]
    public async Task BigQuery_ASourceLimit_IsSentWithTheJob()
    {
        var client = RunningClient();

        await BigQuery(client.Object, """{"ProjectId":"acme-prod","MaximumBytesBilled":5000000}""").ExecuteQueryAsync(Source(DataSourceType.BigQuery), SelectSql, []);

        QueryJobs(client).Single().MaximumBytesBilled.Should().Be(5_000_000);
    }

    [TestCase(5_000_000L, 1_000_000L, 1_000_000L)]
    [TestCase(500_000L, 1_000_000L, 500_000L)]
    public async Task BigQuery_TheDeploymentCeiling_LowersTheSourceLimit(long sourceLimit, long ceiling, long expected)
    {
        var client = RunningClient();
        var provider = BigQuery(client.Object, $$"""{"ProjectId":"acme-prod","MaximumBytesBilled":{{sourceLimit}}}""", new BigQueryOptions { MaxBytesBilled = ceiling });

        await provider.ExecuteQueryAsync(Source(DataSourceType.BigQuery), SelectSql, []);

        QueryJobs(client).Single().MaximumBytesBilled.Should().Be(expected);
    }

    [Test]
    public async Task BigQuery_TheReadOnlyPath_RunsItsJobUnderTheLimit()
    {
        var client = RunningClient();

        await BigQuery(client.Object, """{"ProjectId":"acme-prod"}""").ExecuteReadOnlyQueryAsync(Source(DataSourceType.BigQuery), SelectSql, []);

        QueryJobs(client).Should().ContainSingle(x => x.DryRun != true)
            .Which.MaximumBytesBilled.Should().Be(BigQueryConfiguration.DefaultMaximumBytesBilled);
    }

    [Test]
    public async Task BigQuery_AQueryOverTheBytesLimit_FailsWithoutQuotingTheStatement()
    {
        var client = RunningClient();
        client
            .Setup(x => x.GetQueryResultsAsync(It.IsAny<JobReference>(), It.IsAny<GetQueryResultsOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GoogleApiException("bigquery", $"Query exceeded limit for bytes billed: {SelectSql}")
            {
                HttpStatusCode = HttpStatusCode.BadRequest,
                Error = new RequestError { Errors = [new SingleError { Reason = "bytesBilledLimitExceeded" }] }
            });

        var result = await BigQuery(client.Object, """{"ProjectId":"acme-prod","MaximumBytesBilled":5000000}""").ExecuteQueryAsync(Source(DataSourceType.BigQuery), SelectSql, []);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("5,000,000 bytes");
        result.ErrorMessage.Should().NotContain("orders");
    }

    [Test]
    public async Task BigQuery_ACallerCancellation_CancelsTheJob()
    {
        var client = RunningClient();
        client
            .Setup(x => x.GetQueryResultsAsync(It.IsAny<JobReference>(), It.IsAny<GetQueryResultsOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await BigQuery(client.Object, """{"ProjectId":"acme-prod"}""").ExecuteQueryAsync(Source(DataSourceType.BigQuery), SelectSql, []);

        result.Success.Should().BeFalse();
        client.Verify(x => x.CancelJobAsync(It.Is<JobReference>(y => y.JobId == "job"), It.IsAny<CancelJobOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task BigQuery_AWaitThatTimesOut_CancelsTheJob()
    {
        var client = RunningClient();
        client
            .Setup(x => x.GetQueryResultsAsync(It.IsAny<JobReference>(), It.IsAny<GetQueryResultsOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException());

        var result = await BigQuery(client.Object, """{"ProjectId":"acme-prod","QueryTimeoutSeconds":30}""").ExecuteQueryAsync(Source(DataSourceType.BigQuery), SelectSql, []);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("30 seconds");
        client.Verify(x => x.CancelJobAsync(It.Is<JobReference>(y => y.JobId == "job"), It.IsAny<CancelJobOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task BigQuery_AStoredTimeoutAboveTheMaximum_IsClamped()
    {
        var client = RunningClient();

        await BigQuery(client.Object, """{"ProjectId":"acme-prod","QueryTimeoutSeconds":5000}""").ExecuteQueryAsync(Source(DataSourceType.BigQuery), SelectSql, []);

        var options = client.Invocations
            .Where(x => x.Method.Name == nameof(BigQueryClient.GetQueryResultsAsync))
            .Select(x => (GetQueryResultsOptions?)x.Arguments[1])
            .Single();
        options!.Timeout.Should().Be(TimeSpan.FromSeconds(DataSourceQueryTimeout.MaxSeconds));
    }

    [Test]
    public void BigQueryOptions_ACeilingBelowOne_FailsValidation()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{BigQueryOptions.SectionName}:MaxBytesBilled"] = "0" })
            .Build();
        new BeaconBuilder(services, configuration).AddBigQueryConnector();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<BigQueryOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Test]
    public async Task Databricks_AStatement_IsSubmittedWithoutWaitingSoItCanBeCancelled()
    {
        var handler = new ScriptedHandler(Succeeded(rows: 1));

        await Databricks(handler).ExecuteQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", []);

        handler.SubmittedBody!.RootElement.GetProperty("wait_timeout").GetString().Should().Be("0s");
    }

    [Test]
    public async Task Databricks_ARowCappedRead_SendsTheRowAndByteLimits()
    {
        var handler = new ScriptedHandler(Succeeded(rows: 1));
        var provider = (IRowCappedReadOnlyExecution)Databricks(handler);

        var result = await provider.ExecuteReadOnlyQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", [], 101, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        handler.SubmittedBody!.RootElement.GetProperty("row_limit").GetInt32().Should().Be(101);
        handler.SubmittedBody.RootElement.GetProperty("byte_limit").GetInt64().Should().Be(16L * 1024 * 1024);
    }

    [Test]
    public async Task Databricks_AnUncappedRead_SendsOnlyTheByteLimit()
    {
        var handler = new ScriptedHandler(Succeeded(rows: 1));

        await Databricks(handler).ExecuteReadOnlyQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", []);

        handler.SubmittedBody!.RootElement.TryGetProperty("row_limit", out _).Should().BeFalse();
        handler.SubmittedBody.RootElement.GetProperty("byte_limit").GetInt64().Should().Be(16L * 1024 * 1024);
    }

    [Test]
    public async Task Databricks_AResultCutByTheByteLimit_Fails()
    {
        var handler = new ScriptedHandler(Succeeded(rows: 3, truncated: true));
        var provider = (IRowCappedReadOnlyExecution)Databricks(handler);

        var result = await provider.ExecuteReadOnlyQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", [], 101, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("16 MB");
    }

    [Test]
    public async Task Databricks_AResultCutAtTheRowLimit_IsReturnedForTheCallerToJudge()
    {
        var handler = new ScriptedHandler(Succeeded(rows: 2, truncated: true));
        var provider = (IRowCappedReadOnlyExecution)Databricks(handler);

        var result = await provider.ExecuteReadOnlyQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", [], 2, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().HaveCount(2);
    }

    [Test]
    public async Task Databricks_ACallerCancellation_CancelsTheStatement()
    {
        using var caller = new CancellationTokenSource();
        var handler = new ScriptedHandler("""{"statement_id":"st1","status":{"state":"PENDING"}}""")
        {
            OnPoll = caller.Cancel
        };

        var act = () => Databricks(handler).ExecuteQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", [], caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().Contain(x => x.Method == HttpMethod.Post && x.Path.EndsWith("/statements/st1/cancel"));
    }

    [Test]
    public async Task Databricks_ACallerCancellingDuringSubmission_CancelsTheAcceptedStatement()
    {
        using var caller = new CancellationTokenSource();
        var handler = new ScriptedHandler("""{"statement_id":"st1","status":{"state":"PENDING"}}""")
        {
            OnSubmit = caller.Cancel
        };

        var act = () => Databricks(handler).ExecuteQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", [], caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().Contain(x => x.Method == HttpMethod.Post && x.Path.EndsWith("/statements/st1/cancel"));
        handler.Requests.Should().NotContain(x => x.Method == HttpMethod.Get, "a cancelled caller's statement is not polled");
    }

    [Test]
    public async Task Databricks_AFailedPollAfterRunning_CancelsTheStatement()
    {
        var handler = new ScriptedHandler("""{"statement_id":"st1","status":{"state":"RUNNING"}}""")
        {
            PollReply = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        };

        var result = await Databricks(handler).ExecuteQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", []);

        result.Success.Should().BeFalse();
        handler.Requests.Should().Contain(x => x.Method == HttpMethod.Post && x.Path.EndsWith("/statements/st1/cancel"));
    }

    [Test]
    public async Task Databricks_AnUnreadablePollAfterRunning_CancelsTheStatement()
    {
        var handler = new ScriptedHandler("""{"statement_id":"st1","status":{"state":"RUNNING"}}""")
        {
            PollReply = () => Json("not json")
        };

        var result = await Databricks(handler).ExecuteQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", []);

        result.Success.Should().BeFalse();
        handler.Requests.Should().Contain(x => x.Method == HttpMethod.Post && x.Path.EndsWith("/statements/st1/cancel"));
    }

    [Test]
    public async Task Databricks_APendingStatement_IsPolledAtOnce()
    {
        var handler = new ScriptedHandler("""{"statement_id":"st1","status":{"state":"PENDING"}}""")
        {
            PollAnswer = Succeeded(rows: 1)
        };
        var started = DateTime.UtcNow;

        var result = await Databricks(handler).ExecuteQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", []);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().ContainSingle();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromMilliseconds(450), "the first poll is not delayed");
    }

    [Test]
    public async Task Databricks_APendingStatementWithoutAnId_FailsWithoutPolling()
    {
        var handler = new ScriptedHandler("""{"status":{"state":"PENDING"}}""");

        var result = await Databricks(handler).ExecuteQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", []);

        result.Success.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task Databricks_ATruncatedResultWithoutARowCount_Fails()
    {
        var handler = new ScriptedHandler(Succeeded(rows: 101, truncated: true, reportRowCount: false));
        var provider = (IRowCappedReadOnlyExecution)Databricks(handler);

        var result = await provider.ExecuteReadOnlyQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", [], 101, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("16 MB");
    }

    [Test]
    public async Task Databricks_AnUncappedResultOverTheConnectorLimit_Fails()
    {
        var handler = new ScriptedHandler(Succeeded(rows: 10_001));

        var result = await Databricks(handler).ExecuteReadOnlyQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", []);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("10,000 rows");
    }

    [Test]
    public async Task Databricks_ACappedReadAboveTheConnectorLimit_ReturnsEveryRow()
    {
        var handler = new ScriptedHandler(Succeeded(rows: 10_050));
        var provider = (IRowCappedReadOnlyExecution)Databricks(handler);

        var result = await provider.ExecuteReadOnlyQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", [], 20_000, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().HaveCount(10_050);
    }

    [Test]
    public async Task Databricks_AChunkWithoutRows_FailsTheResult()
    {
        var handler = new ScriptedHandler(Succeeded(rows: 2, nextChunkIndex: 1));

        var result = await Databricks(handler).ExecuteReadOnlyQueryAsync(Source(DataSourceType.Databricks), "SELECT id FROM main.sales.orders", []);

        result.Success.Should().BeFalse();
        handler.Requests.Should().Contain(x => x.Path.EndsWith("/result/chunks/1"));
    }

    [Test]
    public async Task BigQuery_AnUncappedResultOverTheConnectorLimit_Fails()
    {
        var client = RunningClient(resultRows: 10_001);

        var result = await BigQuery(client.Object, """{"ProjectId":"acme-prod"}""").ExecuteReadOnlyQueryAsync(Source(DataSourceType.BigQuery), SelectSql, []);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("10,000 rows");
    }

    [Test]
    public async Task BigQuery_ACappedRead_StopsAtTheCap()
    {
        var client = RunningClient(resultRows: 12_000);
        var provider = (IRowCappedReadOnlyExecution)BigQuery(client.Object, """{"ProjectId":"acme-prod"}""");

        var result = await provider.ExecuteReadOnlyQueryAsync(Source(DataSourceType.BigQuery), SelectSql, [], 11_000, CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().HaveCount(11_000);
    }

    [Test]
    public async Task BigQuery_ACallerCancellation_IsRethrown()
    {
        using var caller = new CancellationTokenSource();
        var client = RunningClient();
        client
            .Setup(x => x.GetQueryResultsAsync(It.IsAny<JobReference>(), It.IsAny<GetQueryResultsOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                caller.Cancel();

                return Task.FromException<BigQueryResults>(new OperationCanceledException(caller.Token));
            });

        var act = () => BigQuery(client.Object, """{"ProjectId":"acme-prod"}""").ExecuteQueryAsync(Source(DataSourceType.BigQuery), SelectSql, [], caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        client.Verify(x => x.CancelJobAsync(It.IsAny<JobReference>(), It.IsAny<CancelJobOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Snowflake_AStatement_RunsUnderAServerSideTimeoutThatIsResetAfterwards()
    {
        var connection = new RecordingDbConnection { RowCount = 1 };
        DbConnectionFactory.Register(DatabaseEngineType.Snowflake, _ => connection);
        var dataSource = new DataSource { Id = 1, Name = "s", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.Snowflake, EncryptedConnectionData = "e" };

        var result = await TestQueryServices.DatabaseProvider().ExecuteReadOnlyQueryAsync(dataSource, SelectSql, []);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var commands = connection.ExecutedCommands.ToList();
        var set = commands.IndexOf($"ALTER SESSION SET STATEMENT_TIMEOUT_IN_SECONDS = {TestQueryServices.QueryOptions.Value.StatementTimeoutSeconds}");
        var query = commands.IndexOf(SelectSql);
        var unset = commands.IndexOf("ALTER SESSION UNSET STATEMENT_TIMEOUT_IN_SECONDS");
        set.Should().BeGreaterThanOrEqualTo(0);
        query.Should().BeGreaterThan(set);
        unset.Should().BeGreaterThan(query);
    }

    [Test]
    public async Task Snowflake_AFailedReset_EmptiesTheConnectionsPool()
    {
        var connection = new RecordingDbConnection { RowCount = 1, FailOnCommandText = x => x.Contains("UNSET") };
        var cleared = new List<string>();
        DbConnectionFactory.Register(DatabaseEngineType.Snowflake, x => connection, x => cleared.Add("cleared"));
        var dataSource = new DataSource { Id = 1, Name = "s", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.Snowflake, EncryptedConnectionData = "e" };

        var result = await TestQueryServices.DatabaseProvider().ExecuteReadOnlyQueryAsync(dataSource, SelectSql, []);

        result.Success.Should().BeTrue(result.ErrorMessage);
        cleared.Should().ContainSingle();
    }

    [Test]
    public async Task Snowflake_AFailedReset_KeepsTheSessionOutOfThePoolBeforeItCloses()
    {
        var connection = new RecordingDbConnection { RowCount = 1, FailOnCommandText = x => x.Contains("UNSET") };
        var discardedWhile = new List<System.Data.ConnectionState>();
        DbConnectionFactory.Register(DatabaseEngineType.Snowflake, x => connection);
        DbConnectionFactory.RegisterDiscard(DatabaseEngineType.Snowflake, x => discardedWhile.Add(x.State));
        var dataSource = new DataSource { Id = 1, Name = "s", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.Snowflake, EncryptedConnectionData = "e" };

        var result = await TestQueryServices.DatabaseProvider().ExecuteReadOnlyQueryAsync(dataSource, SelectSql, []);

        result.Success.Should().BeTrue(result.ErrorMessage);
        discardedWhile.Should().Equal(System.Data.ConnectionState.Open);
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Test]
    public async Task Snowflake_ASuccessfulReset_KeepsTheSessionPooled()
    {
        var connection = new RecordingDbConnection { RowCount = 1 };
        var discarded = 0;
        DbConnectionFactory.Register(DatabaseEngineType.Snowflake, x => connection);
        DbConnectionFactory.RegisterDiscard(DatabaseEngineType.Snowflake, x => discarded++);
        var dataSource = new DataSource { Id = 1, Name = "s", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.Snowflake, EncryptedConnectionData = "e" };

        await TestQueryServices.DatabaseProvider().ExecuteReadOnlyQueryAsync(dataSource, SelectSql, []);

        discarded.Should().Be(0);
    }

    [Test]
    public async Task Snowflake_TheSessionTimeout_IsNotSentToOtherEngines()
    {
        var connection = new RecordingDbConnection { RowCount = 1 };
        DbConnectionFactory.Register(DatabaseEngineType.AzureSynapse, _ => connection);
        var dataSource = new DataSource { Id = 1, Name = "s", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.AzureSynapse, EncryptedConnectionData = "e" };

        await TestQueryServices.DatabaseProvider().ExecuteReadOnlyQueryAsync(dataSource, SelectSql, []);

        connection.ExecutedCommands.Should().NotContain(x => x.Contains("STATEMENT_TIMEOUT_IN_SECONDS"));
    }

    [Test]
    public async Task Api_AResponseOverTenMegabytes_FailsTheQuery()
    {
        var clients = Clients(_ => Json(new string('x', 10 * 1024 * 1024 + 1)), out _);

        var result = await Api(clients).ExecuteQueryAsync(Source(DataSourceType.Api), ApiQuery(), []);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("10 MB");
    }

    [Test]
    public async Task Api_AResponseWithinTheCap_IsRead()
    {
        var clients = Clients(_ => Json("""{"data":[{"id":1}]}"""), out _);

        var result = await Api(clients).ExecuteQueryAsync(Source(DataSourceType.Api), ApiQuery(), []);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Should().ContainSingle();
    }

    [TestCase(0, 30)]
    [TestCase(-5, 30)]
    [TestCase(1, 1)]
    [TestCase(45, 45)]
    [TestCase(600, 120)]
    public async Task Api_TheStoredTimeout_IsClamped(int stored, int expected)
    {
        var clients = Clients(_ => Json("""{"data":[]}"""), out var made);

        await Api(clients, stored).ExecuteQueryAsync(Source(DataSourceType.Api), ApiQuery(), []);

        made.Should().ContainSingle().Which.Timeout.Should().Be(TimeSpan.FromSeconds(expected));
    }

    [Test]
    public async Task Api_ABodyThatNeverEnds_FailsAtTheTimeout()
    {
        var clients = Clients(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }, out _);

        var result = await Api(clients, timeoutSeconds: 1).ExecuteQueryAsync(Source(DataSourceType.Api), ApiQuery(), []);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("did not answer within 1 seconds");
    }

    [Test]
    public async Task Api_ACallerCancellation_IsRethrown()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        var clients = Clients(_ => Json("""{"data":[]}"""), out _);

        var act = () => Api(clients).ExecuteQueryAsync(Source(DataSourceType.Api), ApiQuery(), [], caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Api_TheResponseCharset_IsHonoured()
    {
        var body = Encoding.Latin1.GetBytes("""{"data":[{"name":"café"}]}""");
        var clients = Clients(_ =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "iso-8859-1" };

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }, out _);

        var result = await Api(clients).ExecuteQueryAsync(Source(DataSourceType.Api), ApiQuery(), []);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Rows.Single()["name"]?.ToString().Should().Be("café");
    }

    [Test]
    public async Task OpenApiImport_ABodyThatNeverEnds_FailsAtTheDeadline()
    {
        var clients = Clients(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }, out _);
        var service = new OpenApiImportService(clients, NullLogger<OpenApiImportService>.Instance) { SpecFetchTimeout = TimeSpan.FromMilliseconds(200) };

        var act = () => service.ImportAsync("https://specs.example.test/openapi.json", null, CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconException>()).Which.Message.Should().Contain("not fetched within");
    }

    [Test]
    public async Task OpenApiImport_TheSpecFetch_HasItsOwnTimeout()
    {
        var clients = Clients(_ => Json("""{"openapi":"3.0.1","info":{"title":"t","version":"1"},"paths":{}}"""), out var made);
        var service = new OpenApiImportService(clients, NullLogger<OpenApiImportService>.Instance);

        await service.ImportAsync("https://specs.example.test/openapi.json", null, CancellationToken.None);

        made.Should().ContainSingle().Which.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task OpenApiImport_ADescriptionOverFiftyMegabytes_IsRefused()
    {
        var clients = Clients(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[50 * 1024 * 1024 + 1]) }, out _);
        var service = new OpenApiImportService(clients, NullLogger<OpenApiImportService>.Instance);

        var act = () => service.ImportAsync("https://specs.example.test/openapi.json", null, CancellationToken.None);

        (await act.Should().ThrowAsync<BeaconException>()).Which.Message.Should().Contain("50 MB");
    }

    private static DataSource Source(DataSourceType type) =>
        new() { Id = 2, Name = "warehouse", DataSourceType = type, EncryptedConnectionData = "enc" };

    private static Mock<BigQueryClient> RunningClient(int? resultRows = null)
    {
        var client = new Mock<BigQueryClient>();
        if (resultRows is { } count)
        {
            client
                .Setup(x => x.GetQueryResultsAsync(It.IsAny<JobReference>(), It.IsAny<GetQueryResultsOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new BigQueryResults(
                    client.Object,
                    new GetQueryResultsResponse
                    {
                        JobComplete = true,
                        JobReference = new JobReference { ProjectId = "acme-prod", JobId = "job" },
                        Schema = new TableSchema { Fields = [new TableFieldSchema { Name = "id", Type = "INTEGER" }] },
                        Rows = Enumerable.Range(1, count)
                            .Select(x =>
                                new TableRow { F = [new TableCell { V = x.ToString(System.Globalization.CultureInfo.InvariantCulture) }] })
                            .ToList(),
                        TotalRows = (ulong)count
                    },
                    null,
                    new GetQueryResultsOptions()));
        }

        client
            .Setup(x => x.CreateQueryJobAsync(It.IsAny<string>(), It.IsAny<IEnumerable<BigQueryParameter>>(), It.IsAny<QueryOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BigQueryJob(
                client.Object,
                new Job
                {
                    JobReference = new JobReference { ProjectId = "acme-prod", JobId = "job" },
                    Statistics = new JobStatistics { Query = new JobStatistics2 { StatementType = "SELECT" } }
                }));
        client
            .Setup(x => x.CancelJobAsync(It.IsAny<JobReference>(), It.IsAny<CancelJobOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BigQueryJob(client.Object, new Job { JobReference = new JobReference { ProjectId = "acme-prod", JobId = "job" } }));

        return client;
    }

    // The options of every job the client was asked to create, in order.
    private static List<QueryOptions> QueryJobs(Mock<BigQueryClient> client) =>
        client.Invocations
            .Where(x => x.Method.Name == nameof(BigQueryClient.CreateQueryJobAsync))
            .Select(x => (QueryOptions)x.Arguments[2])
            .Where(x => x.DryRun != true)
            .ToList();

    private static BigQueryProvider BigQuery(BigQueryClient client, string configuration, BigQueryOptions? options = null)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Returns(configuration);

        return new FixedClientBigQueryProvider(client, encryption.Object, options ?? new BigQueryOptions());
    }

    private static DatabricksProvider Databricks(ScriptedHandler handler)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Returns("""{"Host":"h","HttpPath":"/sql/1.0/warehouses/w","Token":"t","Catalog":"main","QueryTimeoutSeconds":30}""");
        var clients = new Mock<IHttpClientFactory>();
        clients
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new DatabricksProvider(clients.Object, TestTransport.Resolver(encryption.Object), new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance), NullLogger<DatabricksProvider>.Instance);
    }

    private static string Succeeded(int rows, bool truncated = false, bool reportRowCount = true, int? nextChunkIndex = null)
    {
        var data = string.Join(",", Enumerable.Range(1, rows).Select(x => $"[\"{x}\"]"));
        var rowCount = reportRowCount ? $"\"total_row_count\":{rows}," : "";
        var next = nextChunkIndex is { } index ? $",\"next_chunk_index\":{index}" : "";

        return $$$"""
            {"statement_id":"st1","status":{"state":"SUCCEEDED"},
             "manifest":{"schema":{"columns":[{"name":"id"}]},{{{rowCount}}}"truncated":{{{(truncated ? "true" : "false")}}}},
             "result":{"data_array":[{{{data}}}]{{{next}}}}}
            """;
    }

    private static ApiProvider Api(IHttpClientFactory clients, int timeoutSeconds = 30)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Returns($$"""{"BaseUrl":"https://api.example.test","OpenApiSpecUrl":"https://api.example.test/openapi.json","TimeoutSeconds":{{timeoutSeconds}}}""");

        return new ApiProvider(
            TestTransport.Resolver(encryption.Object),
            clients,
            new OpenApiImportService(clients, NullLogger<OpenApiImportService>.Instance),
            new JsonResponseTabularizer(),
            NullLogger<ApiProvider>.Instance);
    }

    private static string ApiQuery() =>
        JsonSerializer.Serialize(new
        {
            method = "GET",
            path = "/items",
            resultMapping = new { arrayPath = "$.data" }
        });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static IHttpClientFactory Clients(Func<HttpRequestMessage, HttpResponseMessage> respond, out List<HttpClient> made)
    {
        var clients = new List<HttpClient>();
        made = clients;
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() =>
            {
                var client = new HttpClient(new RespondingHandler(respond));
                clients.Add(client);

                return client;
            });

        return factory.Object;
    }

    private sealed class FixedClientBigQueryProvider(BigQueryClient client, IEncryptionService encryption, BigQueryOptions options)
        : BigQueryProvider(TestTransport.Resolver(encryption), new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance), NullLogger<BigQueryProvider>.Instance, Options.Create(options))
    {
        protected override BigQueryClient CreateClient(BigQueryConfiguration config) => client;
    }

    /// <summary>A response body that never sends a byte: every read waits until it is cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RespondingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>
    /// Answers the statement submission with <c>submitted</c>, every poll with RUNNING (calling <see cref="OnPoll"/> first)
    /// and a cancel with OK, recording each request and the submitted body.
    /// </summary>
    private sealed class ScriptedHandler(string submitted) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];

        public JsonDocument? SubmittedBody { get; private set; }

        public Action? OnPoll { get; init; }

        /// <summary>What a poll answers; RUNNING when null.</summary>
        public string? PollAnswer { get; init; }

        /// <summary>The whole response to a poll, in place of <see cref="PollAnswer"/>.</summary>
        public Func<HttpResponseMessage>? PollReply { get; init; }

        /// <summary>Runs once the submission is accepted, before its answer is read.</summary>
        public Action? OnSubmit { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path));
            if (request.Method == HttpMethod.Post && path.EndsWith("/cancel"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post)
            {
                SubmittedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                OnSubmit?.Invoke();

                return Json(submitted);
            }

            if (path.Contains("/result/chunks/"))
            {
                return Json("{}");
            }

            OnPoll?.Invoke();
            if (PollReply != null)
            {
                return PollReply();
            }

            return Json(PollAnswer ?? """{"statement_id":"st1","status":{"state":"RUNNING"}}""");
        }
    }
}
