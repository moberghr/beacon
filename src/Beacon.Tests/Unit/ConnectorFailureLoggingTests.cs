using System.Data.Common;
using System.Net;
using Beacon.Connector.Api;
using Beacon.Connector.Api.Services;
using Beacon.Connector.BigQuery;
using Beacon.Connector.CloudWatch;
using Beacon.Connector.Databricks;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A connector logs a failed query by exception type and error code only (§1.11): an error message can quote the query,
/// row values or a credential, so neither the message nor the exception object reaches the log. The caller's error
/// message is unchanged. The failure here is raised before anything is sent, so no service is reached.
/// </summary>
[TestFixture]
public class ConnectorFailureLoggingTests
{
    private const int DataSourceId = 9;
    private const string SecretLooking = TestCredentials.ProviderSecretKey;
    private const string Sql = "SELECT secret_column FROM accounts WHERE id = 5";

    [Test]
    public async Task Databricks_LogsTheTypeAndCodeOnly()
    {
        var log = new LogRecorder();
        var provider = new DatabricksProvider(Mock.Of<IHttpClientFactory>(), FailingEncryption(), ReadOnlyValidator(), log.For<DatabricksProvider>());

        var result = await provider.ExecuteQueryAsync(Source(DataSourceType.Databricks), Sql, [], CancellationToken.None);

        AssertLoggedWithoutTheMessage(result, log, "Databricks");
    }

    [Test]
    public async Task BigQuery_LogsTheTypeAndCodeOnly()
    {
        var log = new LogRecorder();
        var provider = new BigQueryProvider(FailingEncryption(), ReadOnlyValidator(), log.For<BigQueryProvider>());

        var result = await provider.ExecuteQueryAsync(Source(DataSourceType.BigQuery), Sql, [], CancellationToken.None);

        AssertLoggedWithoutTheMessage(result, log, "BigQuery");
    }

    [Test]
    public async Task CloudWatch_LogsTheTypeAndCodeOnly()
    {
        var log = new LogRecorder();
        var provider = new CloudWatchProvider(FailingEncryption(), log.For<CloudWatchProvider>());

        var result = await provider.ExecuteQueryAsync(Source(DataSourceType.CloudWatch), "fields @message | filter @message like /secret/", [], CancellationToken.None);

        AssertLoggedWithoutTheMessage(result, log, "CloudWatch");
    }

    [Test]
    public async Task Api_LogsTheTypeAndCodeOnly()
    {
        var log = new LogRecorder();
        var clients = new Mock<IHttpClientFactory>();
        var provider = new ApiProvider(
            FailingEncryption(),
            clients.Object,
            new OpenApiImportService(clients.Object, NullLogger<OpenApiImportService>.Instance),
            new JsonResponseTabularizer(),
            log.For<ApiProvider>());

        var result = await provider.ExecuteQueryAsync(Source(DataSourceType.Api), "{\"method\":\"GET\",\"path\":\"/accounts\"}", [], CancellationToken.None);

        AssertLoggedWithoutTheMessage(result, log, "API");
    }

    [Test]
    public void FailureCodes_ReadsTheCodeNeverTheMessage()
    {
        FailureCodes.ErrorCodeOf(new FakeDbException(SecretLooking, "42P01")).Should().Be("42P01");
        FailureCodes.ErrorCodeOf(new InvalidOperationException(SecretLooking, new HttpRequestException(SecretLooking, null, HttpStatusCode.ServiceUnavailable)))
            .Should().Be("503", "a wrapped exception's code is found");
        FailureCodes.ErrorCodeOf(new InvalidOperationException(SecretLooking)).Should().BeNull();
    }

    private static void AssertLoggedWithoutTheMessage(ProviderQueryResult result, LogRecorder log, string connector)
    {
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(SecretLooking, "the caller's error message is unchanged");
        var entry = log.Entries
            .Where(x => x.Level >= LogLevel.Warning)
            .Should()
            .ContainSingle()
            .Subject;
        entry.Exception.Should().BeNull("the exception carries the message");
        entry.Message.Should().StartWith($"Query execution failed for {connector} data source {DataSourceId} with InvalidOperationException (error code none, at ");
        log.Contains(SecretLooking).Should().BeFalse();
        log.Contains("secret").Should().BeFalse("query text never reaches the log");
    }

    private static IEncryptionService FailingEncryption()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Throws(new InvalidOperationException($"could not read key {SecretLooking}"));

        return encryption.Object;
    }

    private static SqlReadOnlyAstValidator ReadOnlyValidator() => new(NullLogger<SqlReadOnlyAstValidator>.Instance);

    private static DataSource Source(DataSourceType type) =>
        new()
        {
            Id = DataSourceId,
            Name = "source",
            DataSourceType = type,
            EncryptedConnectionData = "encrypted"
        };

    private sealed class FakeDbException(string message, string sqlState) : DbException(message)
    {
        public override string SqlState => sqlState;
    }
}
