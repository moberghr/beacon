using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// An ad-hoc query that fails on the data source: the caller reads the database's error redacted while the source's PII
/// detection is on; the stored history always holds it redacted; the log names its type and code only (§1.11).
/// </summary>
[TestFixture]
public class AdHocQueryErrorRedactionTests
{
    private const int DataSourceId = 3;
    private const string ErrorQuotingAValue = "22P02: invalid input syntax for type integer: \"Ivan Horvat\"";
    private const string Redacted = "Database error 22P02 (data exception): a value could not be converted to integer. The database's message is withheld because it can quote row values.";

    [TestCase(true)]
    [TestCase(false)]
    public async Task AFailureQuotingAValue_ReachesTheCallerByTheSetting_TheHistoryAndTheLogNever(bool piiDetectionOn)
    {
        var history = new List<string?>();
        var log = new LogRecorder();
        var service = Service(piiDetectionOn, history, log);

        var result = await service.ExecuteAdHocQuery(DataSourceId, "SELECT total FROM accounts", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(piiDetectionOn ? Redacted : ErrorQuotingAValue);
        history.Should().ContainSingle().Which.Should().Be(Redacted);
        log.Entries.Should().Contain(x => x.Level == LogLevel.Error);
        log.Contains("Horvat").Should().BeFalse();
    }

    // An error the statement's own code raised, as the database provider passes it on, holds its author's values unquoted:
    // withheld from the caller while detection is on, and from the history always.
    [TestCase("P0001: Customer ivan.horvat@bank.example is blocked", "P0001")]
    [TestCase("45000: Customer ivan.horvat@bank.example is blocked", "45000")]
    [TestCase("Msg 50000, Level 16, State 1: Customer ivan.horvat@bank.example is blocked", "50000")]
    public async Task ARaisedError_IsWithheldFromTheCallerAndTheHistory(string error, string code)
    {
        var history = new List<string?>();
        var service = Service(piiDetectionOn: true, history, new LogRecorder(), error);

        var result = await service.ExecuteAdHocQuery(DataSourceId, "SELECT total FROM accounts", CancellationToken.None);

        var withheld = $"Database error {code} (unrecognised): not an error Beacon recognises. The database's message is withheld because it can quote row values.";
        result.Error.Should().Be(withheld);
        history.Should().ContainSingle().Which.Should().Be(withheld);
    }

    private static DataSourceService Service(bool piiDetectionOn, List<string?> history, LogRecorder log, string error = ErrorQuotingAValue)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "core",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "encrypted"
        };
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(DataSource)] = RecordingBeaconContext.MemorySet([dataSource], saved) },
                saved));

        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = false, ErrorMessage = error });
        var settings = new Mock<ISharedSourceSettings>();
        settings
            .Setup(x => x.GetDataSourceSettingsAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData { EnablePiiDetection = piiDetectionOn });
        var runner = new GatedSqlRunner(
            TestSqlGate.Create(),
            TestQueryServices.Providers(provider.Object),
            TestConfiguredDatabases.None,
            TestQueryServices.QueryOptions,
            settings.Object);

        var executions = new Mock<IManualQueryExecutionLogger>();
        executions
            .Setup(x => x.LogQueryExecutionAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<bool>(), It.IsAny<int?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, double, bool, int?, string?, string?, string?, CancellationToken>((_, _, _, _, _, _, error, _, _) => history.Add(error))
            .Returns(Task.CompletedTask);

        return new DataSourceService(
            factory.Object,
            Mock.Of<IEncryptionService>(),
            TestQueryServices.Providers(provider.Object),
            executions.Object,
            Mock.Of<IBeaconUserContext>(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            runner,
            log.For<DataSourceService>(),
            settings.Object);
    }
}
