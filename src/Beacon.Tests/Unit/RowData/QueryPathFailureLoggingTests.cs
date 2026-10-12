using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Connector.Api;
using Beacon.Connector.Api.Services;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// The query-path logs name a failure by its exception type and error code only (§1.11): a server, conversion or HTTP
/// message can quote the SQL, row values or a credential, so neither the message nor the exception object reaches the
/// log — previews, the in-memory join, the ask pipeline's dry run, and the API connector, which logs the data source of
/// a call and never its path.
/// </summary>
[TestFixture]
public class QueryPathFailureLoggingTests
{
    private const string SecretLooking = TestCredentials.ProviderSecretKey;

    private static readonly ListRequest FirstPage = new Paging { Page = 0, PageSize = 20 };

    [Test]
    public async Task APreviewFailure_LogsTheTypeOnly()
    {
        var log = new LogRecorder();
        var stored = new RecordingStoredQueryPreview { Answer = () => throw new InvalidOperationException($"value '{SecretLooking}' is not an integer") };
        var service = new QueryExecutionPreviewService(
            Mock.Of<IQueryService>(),
            stored,
            TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted),
            log.For<QueryExecutionPreviewService>());

        var query = await service.ExecuteQueryPreview(5, null, FirstPage, CancellationToken.None);
        var step = await service.ExecuteStepPreview(5, 1, [], null, FirstPage, CancellationToken.None);

        query!.Success.Should().BeFalse();
        step!.Success.Should().BeFalse();
        AssertLoggedWithoutTheMessage(log, 2, "InvalidOperationException (error code none, at ");
    }

    [Test]
    public async Task AnInMemoryJoinFailure_LogsTheTypeAndSqliteCodeOnly()
    {
        var log = new LogRecorder();
        using var manager = new InMemoryDatabaseManager(log.For<InMemoryDatabaseManager>());

        var act = () => manager.ExecuteQueryAsync($"SELECT * FROM missing_table WHERE token = '{SecretLooking}'");

        await act.Should().ThrowAsync<Exception>();
        AssertLoggedWithoutTheMessage(log, 1, "SqliteException (error code 1, at ");
    }

    [Test]
    public async Task TheAskPipelinesUnavailableDryRun_LogsTheTypeOnly()
    {
        var log = new LogRecorder();
        var knowledge = new Mock<IKnowledgeGraphService>();
        knowledge
            .Setup(x => x.GetSmartContextForAskAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartSchemaContext { FullContext = "ctx", DatabaseDialect = "PostgreSQL" });
        var generation = new Mock<ISqlGenerationService>();
        generation
            .Setup(x => x.GenerateAsync(It.IsAny<ILlmProvider>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>(), It.IsAny<decimal?>()))
            .ReturnsAsync(new SqlGenerationResult("SELECT 1", []));
        var executor = new Mock<IAskSqlExecutor>();
        executor
            .Setup(x => x.DryRunAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"connection refused for key {SecretLooking}"));
        executor
            .Setup(x => x.ExecuteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AskExecutionResult("### Results (1 rows)\n", null, 1, true));
        var pipeline = new AskSqlPipeline(knowledge.Object, generation.Object, TestSqlGate.Create(), TestRowDataEgress.Discarding(), log.For<AskSqlPipeline>());

        await pipeline.RunAsync(Mock.Of<ILlmProvider>(), 7, 3, "how many?", new McpSettingsData(), executor.Object, new AskSqlPipelineOptions(), CancellationToken.None);

        AssertLoggedWithoutTheMessage(log, 1, "InvalidOperationException (error code none, at ");
    }

    [Test]
    public async Task TheApiConnector_LogsTheDataSourceOfACall_NeverItsPath()
    {
        var log = new LogRecorder();
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Returns("""{"baseUrl":"https://api.example.test","openApiSpecUrl":"https://api.example.test/spec.json"}""");
        var clients = new Mock<IHttpClientFactory>();
        clients
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new FailingHandler()));
        var provider = new ApiProvider(
            encryption.Object,
            clients.Object,
            new OpenApiImportService(clients.Object, NullLogger<OpenApiImportService>.Instance),
            new JsonResponseTabularizer(),
            log.For<ApiProvider>());
        var source = new DataSource { Id = 9, Name = "api", DataSourceType = DataSourceType.Api, EncryptedConnectionData = "encrypted" };

        var result = await provider.ExecuteQueryAsync(source, "{\"method\":\"GET\",\"path\":\"/accounts/HR1210010051863000160?api_key=" + SecretLooking + "\",\"resultMapping\":{\"arrayPath\":\"$.data\"}}", [], CancellationToken.None);

        result.Success.Should().BeFalse();
        log.Entries.Should().Contain(x => x.Level == LogLevel.Information && x.Message == "Executing API query on data source 9");
        log.Contains(SecretLooking).Should().BeFalse();
        log.Contains("HR1210010051863000160").Should().BeFalse();
        log.Contains("/accounts").Should().BeFalse();
    }

    private static void AssertLoggedWithoutTheMessage(LogRecorder log, int count, string named)
    {
        var failures = log.Entries
            .Where(x => x.Level >= LogLevel.Warning)
            .ToList();
        failures.Should().HaveCount(count);
        failures.Should().OnlyContain(x => x.Exception == null, "the exception carries the message");
        failures.Should().OnlyContain(x => x.Message.Contains(named, StringComparison.Ordinal));
        log.Contains(SecretLooking).Should().BeFalse();
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException($"could not reach {request.RequestUri}");
        }
    }

    private sealed record Paging : ListRequest;
}
