using System.Text.RegularExpressions;
using FluentAssertions;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Connector.Api;
using Beacon.Connector.Api.Services;
using Beacon.Connector.BigQuery;
using Beacon.Connector.CloudWatch;
using Beacon.Connector.Databricks;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.DataSourceTransport;

/// <summary>
/// Every connection Beacon makes with a data source's stored connection data goes through
/// <see cref="IDataSourceConnectionResolver"/>, so the transport check applies to each of them: the JSON connectors (API,
/// Databricks, BigQuery, CloudWatch), the metadata scan and the migration writer refuse a stored connection that fails the
/// check under <c>EnforceOnExisting</c> before anything goes over the network. A source scan pins that nothing else reads
/// <c>EncryptedConnectionData</c> to connect.
/// </summary>
[TestFixture]
public class StoredConnectionPathTests
{
    private const int StoredId = 9;

    private const string Host = "leaky-host-7f3a.internal";

    private static readonly string Secret = TestCredentials.ProviderSecretKey;

    // The production files that may name EncryptedConnectionData, and why. Anything else that reads it would connect
    // around the resolver's checks.
    private static readonly Dictionary<string, string> AllowedReaders = new(StringComparer.Ordinal)
    {
        ["src/Beacon.Core/Data/Entities/DataSource.cs"] = "the entity property itself",
        ["src/Beacon.Core/HostData/DataSourceConnectionResolver.cs"] = "the one decrypt that becomes a connection",
        ["src/Beacon.Core/HostData/HostDataSourceSynchronizer.cs"] = "writes a host source's reference (a connection string name, never a credential) and reads it back to compare",
        ["src/Beacon.Core/Services/DataSourceService.cs"] = "encrypts a connection on create and update; its StoredConnection helper decrypts the stored one only to compare an update's connection, target and definition with it, to empty the old connection's driver pool, and for the engine probe a held source gets once at its approval (the probe at approval: transport check, then the engine verifier), never to connect a query",
        ["src/Beacon.Core/Services/DatabaseMetadataService.cs"] = "projects the encrypted value, never decrypted, to tell whether a source's connection changed while its metadata scan ran",
        ["src/Beacon.Core/Services/StoredValueReEncryptionService.cs"] = "re-encrypts stored values under the current key",
        ["src/Beacon.Core/Services/Providers/DataSourceTransportStatus.cs"] = "projects the encrypted value and hands it to the resolver's badge check",
        ["src/Beacon.AI/Services/Ai/AiActor/AiActorService.cs"] = "projects a data source with empty connection data"
    };

    // A decrypt of the stored value itself: only the resolver (a connection), the host sync (a reference) and the data-source
    // service (an update's comparison and pool clear, and a held source's engine probe at approval, through its
    // StoredConnection helper) may.
    private static readonly HashSet<string> AllowedDecrypts = new(StringComparer.Ordinal)
    {
        "src/Beacon.Core/HostData/DataSourceConnectionResolver.cs",
        "src/Beacon.Core/HostData/HostDataSourceSynchronizer.cs",
        "src/Beacon.Core/Services/DataSourceService.cs"
    };

    // A decrypt call, or the data-source service's StoredConnection helper, handed the stored value.
    private static readonly Regex DecryptOfStoredData = new(@"\b(?:Decrypt|StoredConnection)\(\s*[\w.]*EncryptedConnectionData\b", RegexOptions.CultureInvariant);

    [Test]
    public async Task TheApiConnector_RefusesAStoredInsecureConnection_BeforeAnyRequest()
    {
        var clients = new Mock<IHttpClientFactory>();
        var provider = new ApiProvider(
            Resolver($"{{\"baseUrl\":\"http://{Host}\",\"openApiSpecUrl\":\"https://{Host}/spec\",\"auth\":{{\"token\":\"{Secret}\"}}}}", TestTransport.Policy(enforceOnExisting: true)),
            clients.Object,
            new OpenApiImportService(clients.Object, NullLogger<OpenApiImportService>.Instance),
            new JsonResponseTabularizer(),
            NullLogger<ApiProvider>.Instance);

        var test = await provider.TestConnectionAsync(Stored(DataSourceType.Api), CancellationToken.None);
        var query = await provider.ExecuteQueryAsync(Stored(DataSourceType.Api), "{\"method\":\"GET\",\"path\":\"/accounts\"}", [], CancellationToken.None);

        AssertRefused(test.Success, test.ErrorMessage, "BaseUrl");
        query.Success.Should().BeFalse();
        clients.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task TheDatabricksConnector_RefusesAStoredConnectionThatFailsTheCheck_BeforeAnyRequest()
    {
        var clients = new Mock<IHttpClientFactory>();
        var provider = new DatabricksProvider(
            clients.Object,
            Resolver(DatabricksJson, Enforcing(DataSourceType.Databricks)),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            NullLogger<DatabricksProvider>.Instance);

        var test = await provider.TestConnectionAsync(Stored(DataSourceType.Databricks), CancellationToken.None);
        var query = await provider.ExecuteQueryAsync(Stored(DataSourceType.Databricks), "SELECT 1", [], CancellationToken.None);

        AssertRefused(test.Success, test.ErrorMessage, FailingInspector.Option);
        query.Success.Should().BeFalse();
        clients.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task TheDatabricksConnector_StillConnects_WhenTheStoredConnectionIsOnlyWarnedAbout()
    {
        var clients = new Mock<IHttpClientFactory>();
        clients
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new RefusingHandler()));
        var policy = new DataSourceTransportPolicy(
            [new FailingInspector(DataSourceType.Databricks)],
            Options.Create(new DataSourceTransportOptions()),
            NullLogger<DataSourceTransportPolicy>.Instance);
        var provider = new DatabricksProvider(
            clients.Object,
            Resolver(DatabricksJson, policy),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            NullLogger<DatabricksProvider>.Instance);

        await provider.TestConnectionAsync(Stored(DataSourceType.Databricks), CancellationToken.None);

        clients.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Once, "the refusal above comes from the policy, not from the configuration");
    }

    [Test]
    public async Task TheBigQueryConnector_RefusesAStoredConnectionThatFailsTheCheck_BeforeAClientIsMade()
    {
        var provider = new CountingBigQueryProvider(Resolver($"{{\"ProjectId\":\"analytics\",\"ServiceAccountJson\":\"{Secret}\"}}", Enforcing(DataSourceType.BigQuery)));

        var test = await provider.TestConnectionAsync(Stored(DataSourceType.BigQuery), CancellationToken.None);
        var query = await provider.ExecuteQueryAsync(Stored(DataSourceType.BigQuery), "SELECT 1", [], CancellationToken.None);

        AssertRefused(test.Success, test.ErrorMessage, FailingInspector.Option);
        query.Success.Should().BeFalse();
        provider.ClientsMade.Should().Be(0);
    }

    [Test]
    public async Task TheCloudWatchConnector_RefusesAStoredConnectionThatFailsTheCheck_BeforeAClientIsMade()
    {
        var provider = new CloudWatchProvider(
            Resolver($"{{\"Region\":\"eu-west-1\",\"AccessKeyId\":\"AKIAEXAMPLE\",\"SecretAccessKey\":\"{Secret}\"}}", Enforcing(DataSourceType.CloudWatch)),
            NullLogger<CloudWatchProvider>.Instance);

        var test = await provider.TestConnectionAsync(Stored(DataSourceType.CloudWatch), CancellationToken.None);

        // The configuration is read before the client is built, so a refusal there means no client and no request.
        AssertRefused(test.Success, test.ErrorMessage, FailingInspector.Option);
    }

    [Test]
    public async Task TheMetadataScan_RefusesAStoredInsecureConnection_BeforeTheExtractorRuns()
    {
        var world = new ProjectAccessWorld();
        world.Source(StoredId);
        var extractor = new Mock<IDatabaseMetadataExtractor>();
        extractor.SetupGet(x => x.SupportedEngineType).Returns(DatabaseEngineType.PostgreSQL);
        var service = new DatabaseMetadataService(
            world.Factory(),
            Resolver($"Host={Host};Password={Secret};SSL Mode=Require", TestTransport.Policy(enforceOnExisting: true)),
            [extractor.Object],
            Mock.Of<IColumnValueSampler>(),
            Mock.Of<ISharedSourceSettings>(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            NullLogger<DatabaseMetadataService>.Instance);

        var scan = () => service.RefreshMetadataAsync(StoredId, forceRefresh: true, CancellationToken.None);

        (await scan.Should().ThrowAsync<DataSourceTransportException>()).Which.Message.Should().Contain("SSL Mode").And.NotContainAny(Host, Secret);
        extractor.Verify(x => x.ExtractMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void TheWriteConnection_IsRefusedForASourceThatIsNotAWriteTarget_BeforeItIsDecrypted()
    {
        var encryption = new Mock<IEncryptionService>();
        var resolver = TestTransport.Resolver(encryption.Object);

        var act = () => resolver.GetWriteConnectionString(WriteTarget(writeTarget: false));

        act.Should().Throw<InvalidOperationException>().WithMessage("*is not a migration write target*");
        encryption.Verify(x => x.Decrypt(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void TheWriteConnection_ToAWriteTarget_PassesTheTransportCheck()
    {
        var insecure = $"Host={Host};Password={Secret};SSL Mode=Require";
        var verified = $"Host={Host};Password={Secret};SSL Mode=VerifyFull";

        var refused = () => Resolver(insecure, TestTransport.Policy(enforceOnExisting: true)).GetWriteConnectionString(WriteTarget());
        var warned = Resolver(insecure, TestTransport.Policy()).GetWriteConnectionString(WriteTarget());
        var accepted = Resolver(verified, TestTransport.Policy(enforceOnExisting: true)).GetWriteConnectionString(WriteTarget());

        refused.Should().Throw<DataSourceTransportException>().Which.Message.Should().NotContainAny(Host, Secret);
        warned.Should().Be(insecure);
        accepted.Should().Be(verified);
    }

    [Test]
    public void TheReadPaths_RefuseAWriteTarget_BeforeItIsDecrypted()
    {
        var encryption = new Mock<IEncryptionService>();
        var resolver = TestTransport.Resolver(encryption.Object);

        var connectionString = () => resolver.GetConnectionString(WriteTarget());
        var connectionData = () => resolver.GetConnectionData(WriteTarget());
        var badge = () => resolver.InspectTransport(WriteTarget());

        connectionString.Should().Throw<InvalidOperationException>().WithMessage(MigrationWriteTargets.ReadRefusedMessage);
        connectionData.Should().Throw<InvalidOperationException>().WithMessage(MigrationWriteTargets.ReadRefusedMessage);
        badge.Should().Throw<InvalidOperationException>().WithMessage(MigrationWriteTargets.ReadRefusedMessage);
        encryption.Verify(x => x.Decrypt(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void NothingButTheResolver_DecryptsStoredConnectionData()
    {
        var root = RepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(root, x).Replace('\\', '/'))
            .Where(x => !x.StartsWith("src/Beacon.Tests/", StringComparison.Ordinal))
            .Where(x => !x.Contains("/Migrations/", StringComparison.Ordinal))
            .Where(x => !x.Contains("/obj/", StringComparison.Ordinal))
            .Where(x => !x.Contains("/bin/", StringComparison.Ordinal))
            .ToList();

        var readers = new List<string>();
        var decrypts = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            if (!text.Contains("EncryptedConnectionData", StringComparison.Ordinal))
            {
                continue;
            }

            readers.Add(file);
            if (DecryptOfStoredData.IsMatch(text))
            {
                decrypts.Add(file);
            }
        }

        files.Should().Contain("src/Beacon.Core/HostData/DataSourceConnectionResolver.cs", "the scan reads the production sources");
        readers.Should().BeSubsetOf(AllowedReaders.Keys, "a new reader of stored connection data must connect through IDataSourceConnectionResolver");
        decrypts.Should().BeSubsetOf(AllowedDecrypts);
        decrypts.Should().Contain("src/Beacon.Core/HostData/DataSourceConnectionResolver.cs", "the pattern still finds the resolver's own decrypt");
        decrypts.Should().Contain("src/Beacon.Core/Services/DataSourceService.cs", "the pattern sees the StoredConnection helper");
    }

    private const string DatabricksJson = """{"Host":"adb.example","HttpPath":"/sql/1.0/warehouses/w","Token":"t","Catalog":"main","QueryTimeoutSeconds":5}""";

    private static void AssertRefused(bool success, string? errorMessage, string option)
    {
        success.Should().BeFalse();
        errorMessage.Should().StartWith(nameof(DataSourceTransportException)).And.Contain(option).And.NotContainAny(Host, Secret);
    }

    private static IDataSourceTransportPolicy Enforcing(DataSourceType type)
    {
        return new DataSourceTransportPolicy(
            [new FailingInspector(type)],
            Options.Create(new DataSourceTransportOptions { EnforceOnExisting = true }),
            NullLogger<DataSourceTransportPolicy>.Instance);
    }

    private static IDataSourceConnectionResolver Resolver(string decrypted, IDataSourceTransportPolicy policy)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Decrypt(It.IsAny<string>())).Returns(decrypted);

        return TestTransport.Resolver(encryption.Object, policy);
    }

    private static DataSource Stored(DataSourceType type)
    {
        return new DataSource { Id = StoredId, Name = "stored", DataSourceType = type, EncryptedConnectionData = "encrypted" };
    }

    private static DataSource WriteTarget(bool writeTarget = true)
    {
        return new DataSource
        {
            Id = StoredId,
            Name = "destination",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "encrypted",
            AllowMigrationWrites = writeTarget
        };
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run inside the repository");

        return directory!.FullName;
    }

    /// <summary>An inspector that never verifies: what a connector's check says about a connection it refuses.</summary>
    private sealed class FailingInspector(DataSourceType dataSourceType) : IDataSourceTransportInspector
    {
        public const string Option = "TestTransportOption";

        public DataSourceType DataSourceType => dataSourceType;

        public DatabaseEngineType? DatabaseEngineType => null;

        public DataSourceTransportVerdict Inspect(string connectionData) => DataSourceTransportVerdict.Fail(Option, $"Set {Option}");
    }

    private sealed class CountingBigQueryProvider(IDataSourceConnectionResolver resolver)
        : BigQueryProvider(resolver, new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance), NullLogger<BigQueryProvider>.Instance, Microsoft.Extensions.Options.Options.Create(new BigQueryOptions()))
    {
        public int ClientsMade { get; private set; }

        protected override BigQueryClient CreateClient(BigQueryConfiguration config)
        {
            ClientsMade++;

            throw new InvalidOperationException("No client in a unit test.");
        }
    }

    /// <summary>Answers every request with a server error, so nothing leaves the process.</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }
}
