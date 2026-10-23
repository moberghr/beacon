using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="GatedSqlRunner"/>: the gate with read-only enforcement always on, then the provider's read-only path, within
/// the caller's cancellation and a bounded statement timeout. A recording provider; no database (§4.7).
/// </summary>
[TestFixture]
public class GatedSqlRunnerTests
{
    private static readonly DataSource Source = new()
    {
        Id = 3,
        Name = "core",
        DataSourceType = DataSourceType.Database,
        DatabaseEngineType = DatabaseEngineType.PostgreSQL,
        EncryptedConnectionData = "encrypted"
    };

    [TestCase("UPDATE accounts SET balance = 0")]
    [TestCase("SELECT 1 AS id /*!50000 UPDATE accounts SET balance = 0 */")]
    [TestCase("-- fix balances\nUPDATE accounts SET balance = 0")]
    [TestCase("SELECT setval('accounts_id_seq', 1)")]
    [TestCase("SELECT id FROM hr.public.salaries")]
    public async Task ARefusedStatement_ThrowsBeforeTheProviderIsAsked(string sql)
    {
        var provider = new Mock<IDataSourceProvider>(MockBehavior.Strict);
        var runner = Runner(provider.Object, TestConfiguredDatabases.Of("sales"));

        var act = () => runner.RunAsync(new GatedSqlRequest(Source, sql, new Dictionary<string, object?>(), 10, TestRowSecurity.NotApplicable), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        provider.Invocations.Should().BeEmpty();
    }

    // Judged first as if no database were configured, then again against the configured one: a catalog read in the configured
    // database is still refused, as a catalog read.
    [TestCase("SELECT * FROM sales.pg_catalog.pg_stats", "system_catalog")]
    [TestCase("SELECT * FROM hr.pg_catalog.pg_stats", "cross_database")]
    public void ACatalogReadQualifiedWithADatabase_IsRefused(string sql, string code)
    {
        var runner = Runner(new Mock<IDataSourceProvider>(MockBehavior.Strict).Object, TestConfiguredDatabases.Of("sales"));

        var report = runner.Evaluate(new GatedSqlRequest(Source, sql, new Dictionary<string, object?>(), 10, TestRowSecurity.NotApplicable));

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(code);
    }

    [Test]
    public async Task TheStatementRuns_OnTheReadOnlyPath_CappedInTheSql_WithItsParameters()
    {
        var provider = Answering(new ProviderQueryResult { Success = true, Rows = [new Dictionary<string, object?> { ["id"] = 1 }] });
        var runner = Runner(provider.Object, TestConfiguredDatabases.None);

        var run = await runner.RunAsync(
            new GatedSqlRequest(Source, "SELECT id FROM accounts WHERE id = @p0", new Dictionary<string, object?> { ["p0"] = 5 }, 11, TestRowSecurity.NotApplicable),
            CancellationToken.None);

        run.Rows.Should().ContainSingle();
        provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(Source, "SELECT id FROM accounts WHERE id = @p0 LIMIT 11", It.Is<Dictionary<string, object?>>(y => (int)y["p0"]! == 5), It.IsAny<CancellationToken>()),
            Times.Once);
        provider.Verify(x => x.ExecuteQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase("SELECT id FROM accounts", 0)]
    [TestCase("SELECT id FROM sales.public.accounts", 1)]
    public async Task TheConfiguredDatabase_IsReadOnlyWhenTheStatementNamesADatabase(string sql, int lookups)
    {
        var resolver = new CountingResolver("sales");
        var runner = Runner(Answering(new ProviderQueryResult { Success = true }).Object, resolver);

        await runner.RunAsync(new GatedSqlRequest(Source, sql, new Dictionary<string, object?>(), 10, TestRowSecurity.NotApplicable), CancellationToken.None);

        resolver.Lookups.Should().Be(lookups);
    }

    [Test]
    public async Task AFailureOnTheDataSource_ThrowsWithTheProvidersMessage()
    {
        const string error = "42P01: relation \"accounts\" does not exist";
        var runner = Runner(Answering(new ProviderQueryResult { Success = false, ErrorMessage = error }).Object, TestConfiguredDatabases.None);

        var act = () => runner.RunAsync(new GatedSqlRequest(Source, "SELECT id FROM accounts", new Dictionary<string, object?>(), 10, TestRowSecurity.NotApplicable), CancellationToken.None);

        await act.Should().ThrowAsync<QueryExecutionFailedException>().WithMessage(error);
    }

    // A provider catches the connection resolver's refusal of a source whose connection waits for approval and reports its
    // fixed message: the run is refused as that, so what records it says why.
    [Test]
    public async Task AConnectionWaitingForApproval_IsRefusedAsSuch()
    {
        var runner = Runner(Answering(new ProviderQueryResult { Success = false, ErrorMessage = DataSourceAwaitingApprovalException.RefusalMessage }).Object, TestConfiguredDatabases.None);

        var act = () => runner.RunAsync(new GatedSqlRequest(Source, "SELECT id FROM accounts", new Dictionary<string, object?>(), 10, TestRowSecurity.NotApplicable), CancellationToken.None);

        (await act.Should().ThrowAsync<DataSourceAwaitingApprovalException>()).Which.DataSourceId.Should().Be(Source.Id);
    }

    // The database's error quotes a row value: while the source's effective PII detection is on (the request's settings,
    // else the source's tightened global settings) the failure carries it redacted; with detection off it is as it was.
    [TestCase(true, null, true)]
    [TestCase(false, null, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    public async Task AFailureQuotingAValue_IsRedactedWhilePiiDetectionIsOn(bool sourceDetects, bool? requestDetects, bool redacted)
    {
        const string error = "22P02: invalid input syntax for type integer: \"Ivan Horvat\"";
        var settings = new Mock<ISharedSourceSettings>();
        settings
            .Setup(x => x.GetDataSourceSettingsAsync(Source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData { EnablePiiDetection = sourceDetects });
        var runner = new GatedSqlRunner(
            TestSqlGate.Create(),
            TestQueryServices.Providers(Answering(new ProviderQueryResult { Success = false, ErrorMessage = error }).Object),
            TestConfiguredDatabases.None,
            TestQueryServices.QueryOptions,
            settings.Object);
        var pii = requestDetects is { } detects ? new McpSettingsData { EnablePiiDetection = detects } : null;

        var act = () => runner.RunAsync(new GatedSqlRequest(Source, "SELECT id FROM accounts", new Dictionary<string, object?>(), 10, TestRowSecurity.NotApplicable, pii), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<QueryExecutionFailedException>();
        if (redacted)
        {
            thrown.Which.Message.Should().Be("Database error 22P02 (data exception): a value could not be converted to integer. The database's message is withheld because it can quote row values.");
            return;
        }

        thrown.Which.Message.Should().Be(error);
    }

    [Test]
    public async Task ACancelledRequest_ThrowsAndNothingRuns()
    {
        var provider = new Mock<IDataSourceProvider>(MockBehavior.Strict);
        var runner = Runner(provider.Object, TestConfiguredDatabases.None);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => runner.RunAsync(new GatedSqlRequest(Source, "SELECT id FROM accounts", new Dictionary<string, object?>(), 10, TestRowSecurity.NotApplicable), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.Invocations.Should().BeEmpty();
    }

    // The runner's own timeout ends the statement unless the request asks for a shorter one: a request without one, a zero,
    // negative or longer one keeps the runner's. The timers are far apart, so which one fired is clear on a busy host; the
    // caller's token ends a statement no timer ends, so such a run fails instead of hanging.
    [TestCase(null, 200, false)]
    [TestCase(0, 200, false)]
    [TestCase(-50, 200, false)]
    [TestCase(5_000, 200, false)]
    [TestCase(50, 5_000, true)]
    public async Task AStatementOverItsTimeout_IsReportedAsTimedOut_AndOnlyAShorterRequestedTimeoutWins(int? requestedMs, int runnerMs, bool requestedFires)
    {
        var observed = TimeSpan.Zero;
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns(async (DataSource _, string _, Dictionary<string, object?> _, CancellationToken token) =>
            {
                var started = DateTime.UtcNow;
                await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
                observed = DateTime.UtcNow - started;

                return new ProviderQueryResult { Success = false, ErrorMessage = "cancelled" };
            });
        var runner = new GatedSqlRunner(TestSqlGate.Create(), TestQueryServices.Providers(provider.Object), TestConfiguredDatabases.None, TestQueryServices.QueryOptions)
        {
            StatementTimeout = TimeSpan.FromMilliseconds(runnerMs)
        };
        var requested = requestedMs is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null;
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = await runner.RunAsync(new GatedSqlRequest(Source, "SELECT id FROM accounts", new Dictionary<string, object?>(), 10, Timeout: requested, RowSecurity: TestRowSecurity.NotApplicable), caller.Token);

        run.TimedOut.Should().BeTrue();
        run.Rows.Should().BeEmpty();
        var fired = requestedFires ? requestedMs!.Value : runnerMs;
        observed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(fired - 20));
        if (requestedFires)
        {
            observed.Should().BeLessThan(TimeSpan.FromMilliseconds(runnerMs), "the shorter requested timeout ended the statement");
        }
        else if (requestedMs > runnerMs)
        {
            observed.Should().BeLessThan(TimeSpan.FromMilliseconds(requestedMs.Value), "a longer requested timeout never extends the runner's");
        }
    }

    [Test]
    public void TheStatementTimeout_IsBeaconQueryStatementTimeoutSeconds_WhichStartupValidates()
    {
        var providers = TestQueryServices.Providers(Mock.Of<IDataSourceProvider>());
        var configured = new GatedSqlRunner(
            TestSqlGate.Create(),
            providers,
            TestConfiguredDatabases.None,
            Microsoft.Extensions.Options.Options.Create(new QueryExecutionOptions { StatementTimeoutSeconds = 300 }));
        var unconfigured = new GatedSqlRunner(TestSqlGate.Create(), providers, TestConfiguredDatabases.None, TestQueryServices.QueryOptions);
        var validator = new QueryExecutionOptionsValidator();

        configured.StatementTimeout.Should().Be(TimeSpan.FromSeconds(300));
        unconfigured.StatementTimeout.Should().Be(TimeSpan.FromSeconds(120));
        validator.Validate(null, new QueryExecutionOptions { StatementTimeoutSeconds = 0 }).Failed.Should().BeTrue();
        validator.Validate(null, new QueryExecutionOptions { StatementTimeoutSeconds = 1 }).Succeeded.Should().BeTrue();
        validator.Validate(null, new QueryExecutionOptions { StatementTimeoutSeconds = 3600 }).Succeeded.Should().BeTrue();
        validator.Validate(null, new QueryExecutionOptions { StatementTimeoutSeconds = 3601 }).FailureMessage.Should().Contain("Beacon:Query:StatementTimeoutSeconds");
    }

    [TestCase("-- Revenue by month\nSELECT id FROM accounts")]
    [TestCase("/* delete old rows first */ SELECT id FROM accounts")]
    public async Task AStatementExplainedInComments_Runs(string sql)
    {
        var provider = Answering(new ProviderQueryResult { Success = true });
        var runner = Runner(provider.Object, TestConfiguredDatabases.None);

        await runner.RunAsync(new GatedSqlRequest(Source, sql, new Dictionary<string, object?>(), 10, TestRowSecurity.NotApplicable), CancellationToken.None);

        provider.Verify(
            x => x.ExecuteReadOnlyQueryAsync(Source, It.Is<string>(y => y.Contains("FROM accounts")), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task AProviderThatCanStopAtTheCap_IsAskedToReadNoMoreThanTheRequestsCap()
    {
        var provider = new CappedProvider();
        var runner = Runner(provider, TestConfiguredDatabases.None);

        await runner.RunAsync(new GatedSqlRequest(Source, "SELECT id FROM accounts LIMIT 1000000", new Dictionary<string, object?>(), 11, TestRowSecurity.NotApplicable), CancellationToken.None);
        await runner.RunAsync(new GatedSqlRequest(Source, "SELECT id FROM accounts", new Dictionary<string, object?>(), 5, TestRowSecurity.NotApplicable), CancellationToken.None);

        provider.Calls.Should().Equal(("SELECT id FROM accounts LIMIT 11", 11), ("SELECT id FROM accounts LIMIT 5", 5));
    }

    private static GatedSqlRunner Runner(IDataSourceProvider provider, IConfiguredDatabaseResolver databases) =>
        new(TestSqlGate.Create(), TestQueryServices.Providers(provider), databases, TestQueryServices.QueryOptions);

    private static Mock<IDataSourceProvider> Answering(ProviderQueryResult result)
    {
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        return provider;
    }

    /// <summary>A provider whose read-only path can stop at a row cap; records which path each statement took.</summary>
    private sealed class CappedProvider : IDataSourceProvider, IRowCappedReadOnlyExecution
    {
        public List<(string Sql, int? MaxRows)> Calls { get; } = [];

        public DataSourceType SupportedType => DataSourceType.Database;

        public string GetQueryLanguageName() => "SQL";

        public Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(DataSource dataSource, string query, Dictionary<string, object?> parameters, int maxRows, CancellationToken cancellationToken)
        {
            Calls.Add((query, maxRows));

            return Task.FromResult(new ProviderQueryResult { Success = true });
        }

        public Task<ProviderQueryResult> ExecuteReadOnlyQueryAsync(DataSource dataSource, string query, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default)
        {
            Calls.Add((query, null));

            return Task.FromResult(new ProviderQueryResult { Success = true });
        }

        public Task<ProviderQueryResult> ExecuteQueryAsync(DataSource dataSource, string query, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("only the read-only path may run");

        public Task<ConnectionTestResult> TestConnectionAsync(DataSource dataSource, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DataSourceMetadata> GetMetadataAsync(DataSource dataSource, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<QueryValidationResult> ValidateQueryAsync(DataSource dataSource, string query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CountingResolver(string database) : IConfiguredDatabaseResolver
    {
        public int Lookups { get; private set; }

        public string? Resolve(DataSource dataSource)
        {
            Lookups++;

            return database;
        }
    }
}
