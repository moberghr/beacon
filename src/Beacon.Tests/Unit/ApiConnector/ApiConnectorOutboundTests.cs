using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Connector.Api;
using Beacon.Connector.Api.Models;
using Beacon.Connector.Api.Services;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ApiConnector;

/// <summary>
/// The REST API connector's outbound requests stay on the configured API: a query cannot set routing, method-override,
/// framing or credential headers, nor control characters in a header; its path cannot leave the base URL's path; redirects
/// are not followed; the connection test sends the credentials only to the base URL's origin; the data source's own
/// credentials are removed from what the API answers; and a new configuration cannot send the API key in the query string,
/// while a stored one keeps working with a Warning. The connector's client logs no request.
/// </summary>
[TestFixture]
public class ApiConnectorOutboundTests
{
    private const string BaseUrl = "https://api.example.test";

    private static readonly string Secret = TestCredentials.ProviderSecretKey + "+/=";

    [TestCase("Host")]
    [TestCase("host")]
    [TestCase("X-Forwarded-Host")]
    [TestCase("X-Forwarded-For")]
    [TestCase("Forwarded")]
    [TestCase("X-Original-URL")]
    [TestCase("X-Rewrite-URL")]
    [TestCase("X-HTTP-Method-Override")]
    [TestCase("X-HTTP-Method")]
    [TestCase("X-Method-Override")]
    [TestCase("Transfer-Encoding")]
    [TestCase("Content-Length")]
    [TestCase("Connection")]
    [TestCase("Authorization")]
    [TestCase("Proxy-Authorization")]
    [TestCase("Cookie")]
    public void CreateRequest_RefusesAHeaderThatRoutesOrAuthenticatesTheRequest(string name)
    {
        var act = () => ApiHttpClientHelper.CreateRequest(Config(), Query("/items", headers: new() { [name] = "value" }));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{name}'*");
    }

    [Test]
    public void CreateRequest_RefusesTheApiKeysOwnHeader()
    {
        var config = Config(new ApiAuthConfig { Type = ApiAuthType.ApiKey, ApiKeyName = "X-Api-Key", ApiKeyValue = Secret });

        var act = () => ApiHttpClientHelper.CreateRequest(config, Query("/items", headers: new() { ["x-api-key"] = "other" }));

        act.Should().Throw<InvalidOperationException>();
    }

    [TestCase("X-Trace\r\nHost", "a")]
    [TestCase("X Trace", "a")]
    [TestCase("", "a")]
    [TestCase("X-Trace", "a\r\nHost: other.example.test")]
    [TestCase("X-Trace", "a\nb")]
    [TestCase("X-Trace", "a\0b")]
    public void CreateRequest_RefusesAnInvalidHeaderNameOrAControlCharacterInItsValue(string name, string value)
    {
        var act = () => ApiHttpClientHelper.CreateRequest(Config(), Query("/items", headers: new() { [name] = value }));

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void CreateRequest_KeepsAnOrdinaryHeader_AndTheDataSourcesCredentials()
    {
        var config = Config(new ApiAuthConfig { Type = ApiAuthType.Bearer, Token = Secret });

        using var request = ApiHttpClientHelper.CreateRequest(config, Query("/items", headers: new() { ["Accept-Language"] = "hr\tx" }));

        request.Headers.GetValues("Accept-Language").Should().ContainSingle().Which.Should().Be("hr\tx");
        request.Headers.Authorization!.Parameter.Should().Be(Secret);
    }

    [TestCase("/../admin")]
    [TestCase("/items/./x")]
    [TestCase("/items/..")]
    [TestCase("/%2e%2e/admin")]
    [TestCase("/%2E%2E/admin")]
    [TestCase("/%252e%252e/admin")]
    [TestCase("/..%2fadmin")]
    [TestCase("/items\\..\\admin")]
    [TestCase("/items#fragment")]
    [TestCase("/items%0d%0aX-Injected")]
    public void CreateRequest_RefusesAPathThatLeavesTheBaseUrl(string path)
    {
        var act = () => ApiHttpClientHelper.CreateRequest(Config(baseUrl: "https://api.example.test/v1"), Query(path));

        act.Should().Throw<InvalidOperationException>();
    }

    [TestCase("..")]
    [TestCase("../admin")]
    [TestCase(".")]
    public void CreateRequest_RefusesAPathParameterThatIsADotSegment(string value)
    {
        var query = Query("/items/{id}");
        query.Parameters!.Path["id"] = value;

        var act = () => ApiHttpClientHelper.CreateRequest(Config(baseUrl: "https://api.example.test/v1"), query);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void CreateRequest_ComposesAUrlUnderTheBasePath()
    {
        var query = Query("/items/{id}");
        query.Parameters!.Path["id"] = "a/b";
        query.Parameters.Query["page"] = "2";

        using var request = ApiHttpClientHelper.CreateRequest(Config(baseUrl: "https://api.example.test/v1/"), query);

        request.RequestUri!.AbsoluteUri.Should().Be("https://api.example.test/v1/items/a%2Fb?page=2");
    }

    [Test]
    public void AddApiConnector_RegistersAClientThatFollowsNoRedirect_LogsNoRequest_AndMarksEachRequest()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new BeaconBuilder(services, new ConfigurationBuilder().Build()).AddApiConnector();
        using var provider = services.BuildServiceProvider();

        var chain = new List<HttpMessageHandler>();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ApiConnectorHttpClient.Name);
        for (HttpMessageHandler? current = handler; current != null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(current);
        }

        var primary = chain.OfType<SocketsHttpHandler>().Should().ContainSingle().Which;
        primary.AllowAutoRedirect.Should().BeFalse();
        primary.UseCookies.Should().BeFalse();
        chain.Should().ContainSingle(x => x is ApiConnectorRequestMarker);
        chain.Should().NotContain(x => x is LoggingHttpMessageHandler || x is LoggingScopeHttpMessageHandler);
    }

    [Test]
    public async Task TheRequestMarker_MarksTheRequest_ForTracingFilters()
    {
        HttpRequestMessage? seen = null;
        using var client = new HttpClient(new ApiConnectorRequestMarker
        {
            InnerHandler = new RespondingHandler(x =>
            {
                seen = x;

                return new HttpResponseMessage(HttpStatusCode.OK);
            })
        });

        await client.GetAsync(BaseUrl + "/items");

        ApiConnectorRequests.IsApiConnectorRequest(seen!).Should().BeTrue();
        ApiConnectorRequests.IsApiConnectorRequest(new HttpRequestMessage()).Should().BeFalse();
    }

    [Test]
    public async Task ExecuteQuery_UsesTheConnectorsClient()
    {
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        clients
            .Setup(x => x.CreateClient(ApiConnectorHttpClient.Name))
            .Returns(() => new HttpClient(new RespondingHandler(_ => Json("""{"data":[{"id":1}]}"""))));

        var result = await Provider(clients.Object, ConfigJson()).ExecuteQueryAsync(Source(), QueryJson(), []);

        result.Success.Should().BeTrue(result.ErrorMessage);
        clients.Verify(x => x.CreateClient(ApiConnectorHttpClient.Name), Times.Once);
    }

    [Test]
    public async Task ExecuteQuery_ARedirectAnswer_FailsAndSaysSo()
    {
        var requests = new List<HttpRequestMessage>();
        var clients = Clients(x =>
        {
            requests.Add(x);
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://elsewhere.example.test/items");

            return response;
        });

        var result = await Provider(clients, ConfigJson()).ExecuteQueryAsync(Source(), QueryJson(), []);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("redirect").And.Contain("302");
        requests.Should().ContainSingle();
    }

    [Test]
    public async Task TestConnection_ADescriptionOnAnotherHost_IsFetchedWithoutTheCredentials()
    {
        var requests = new List<HttpRequestMessage>();
        var clients = Clients(x =>
        {
            requests.Add(x);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var config = ConfigJson(new { type = "Bearer", token = Secret }, specUrl: "https://specs.example.org/openapi.json");

        var result = await Provider(clients, config).TestConnectionAsync(Source());

        result.Success.Should().BeTrue(result.ErrorMessage);
        requests.Should().ContainSingle().Which.Headers.Authorization.Should().BeNull();
    }

    [TestCase("https://specs.example.org/openapi.json")]
    [TestCase("http://api.example.test/openapi.json")]
    [TestCase("https://api.example.test:8443/openapi.json")]
    public async Task TestConnection_ADescriptionOnAnotherOrigin_GetsNoHeaderApiKey(string specUrl)
    {
        var requests = new List<HttpRequestMessage>();
        var clients = Clients(x =>
        {
            requests.Add(x);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var config = ConfigJson(new { type = "ApiKey", apiKeyName = "X-Api-Key", apiKeyValue = Secret }, specUrl: specUrl);

        await Provider(clients, config).TestConnectionAsync(Source());

        requests.Should().ContainSingle().Which.Headers.Contains("X-Api-Key").Should().BeFalse();
    }

    [Test]
    public async Task TestConnection_ADescriptionOnTheBaseUrlsOrigin_IsFetchedWithTheCredentials()
    {
        var requests = new List<HttpRequestMessage>();
        var clients = Clients(x =>
        {
            requests.Add(x);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var config = ConfigJson(new { type = "Bearer", token = Secret }, specUrl: "https://API.example.test/docs/openapi.json");

        await Provider(clients, config).TestConnectionAsync(Source());

        requests.Should().ContainSingle().Which.Headers.Authorization!.Parameter.Should().Be(Secret);
    }

    [Test]
    public async Task TestConnection_ARedirectAnswer_FailsAndSaysSo()
    {
        var clients = Clients(_ => new HttpResponseMessage(HttpStatusCode.MovedPermanently));

        var result = await Provider(clients, ConfigJson()).TestConnectionAsync(Source());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("redirect").And.Contain("301");
    }

    [Test]
    public async Task ExecuteQuery_AnAnswerEchoingTheApiKey_ReturnsItMasked()
    {
        var escaped = Uri.EscapeDataString(Secret);
        var body = $$"""
            {"data":[{
              "self":"https://api.example.test/items?api_key={{escaped}}",
              "header":"{{Secret}}",
              "nested":{"key":"{{Secret}}"},
              "count":3
            }]}
            """;
        var clients = Clients(_ => Json(body));
        var config = ConfigJson(new { type = "ApiKey", apiKeyName = "X-Api-Key", apiKeyValue = Secret });

        var result = await Provider(clients, config).ExecuteQueryAsync(Source(), QueryJson(), []);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var row = result.Rows.Should().ContainSingle().Which;
        row["self"].Should().Be("https://api.example.test/items?api_key=***");
        row["header"].Should().Be("***");
        ((string)row["nested"]!).Should().Contain("***").And.NotContain(TestCredentials.ProviderSecretKey);
        row["count"].Should().Be(3L);
    }

    [Test]
    public async Task ExecuteQuery_AnAnswerEchoingTheBasicOrBearerCredentials_ReturnsThemMasked()
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"reader:{Secret}"));
        var body = $$"""{"data":[{"basic":"Basic {{basic}}","password":"{{Secret}}"}]}""";
        var clients = Clients(_ => Json(body));
        var config = ConfigJson(new { type = "Basic", username = "reader", password = Secret });

        var result = await Provider(clients, config).ExecuteQueryAsync(Source(), QueryJson(), []);

        var row = result.Rows.Should().ContainSingle().Which;
        row["basic"].Should().Be("Basic ***");
        row["password"].Should().Be("***");
    }

    [Test]
    public void ANewConfiguration_WithTheApiKeyInTheQueryString_IsRefused()
    {
        var provider = Provider(Clients(_ => Json("{}")), ConfigJson());

        var refused = () => provider.EnsureNewConnectionDataAllowed(ConfigJson(new { type = "ApiKey", apiKeyName = "api_key", apiKeyValue = Secret, apiKeyLocation = "Query" }));
        var header = () => provider.EnsureNewConnectionDataAllowed(ConfigJson(new { type = "ApiKey", apiKeyName = "X-Api-Key", apiKeyValue = Secret, apiKeyLocation = "Header" }));

        refused.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("ApiKeyLocation").And.NotContain(Secret);
        header.Should().NotThrow();
    }

    [Test]
    public async Task AStoredConfiguration_WithTheApiKeyInTheQueryString_KeepsWorking_WithOneWarning_IdOnly()
    {
        using var log = new LogRecorder();
        var requests = new List<HttpRequestMessage>();
        var clients = Clients(x =>
        {
            requests.Add(x);

            return Json("""{"data":[{"id":1}]}""");
        });
        var config = ConfigJson(new { type = "ApiKey", apiKeyName = "api_key", apiKeyValue = Secret, apiKeyLocation = "Query" });
        var provider = Provider(clients, config, log.For<ApiProvider>());
        var source = Source(id: 731_004);

        var first = await provider.ExecuteQueryAsync(source, QueryJson(), []);
        var second = await provider.ExecuteQueryAsync(source, QueryJson(), []);

        first.Success.Should().BeTrue(first.ErrorMessage);
        second.Success.Should().BeTrue(second.ErrorMessage);
        requests.Should().HaveCount(2).And.OnlyContain(x => x.RequestUri!.Query.Contains("api_key="));
        log.Entries.Where(x => x.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.FullText.Should().Contain("731004").And.NotContain(Secret).And.NotContain(Uri.EscapeDataString(Secret));
    }

    [Test]
    public async Task CreatingOrTestingAnApiSource_WithTheApiKeyInTheQueryString_IsRefused_AndNothingIsSaved()
    {
        var saved = new List<object>();
        var service = Service(saved);
        var data = new DataSourceData
        {
            Name = "api",
            DataSourceType = DataSourceType.Api,
            ConnectionString = ConfigJson(new { type = "ApiKey", apiKeyName = "api_key", apiKeyValue = Secret, apiKeyLocation = "Query" })
        };

        var create = () => service.CreateDataSource(data, CancellationToken.None);
        var test = await service.TestConnectionAsync(data, CancellationToken.None);

        (await create.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(ApiProvider.QueryKeyRefusedMessage);
        saved.Should().BeEmpty();
        test.Success.Should().BeFalse();
        test.Message.Should().StartWith("Connection refused:").And.NotContain(Secret);
    }

    [Test]
    public async Task CreatingAnApiSource_WithTheApiKeyInAHeader_IsSaved()
    {
        var saved = new List<object>();
        var data = new DataSourceData
        {
            Name = "api",
            DataSourceType = DataSourceType.Api,
            ConnectionString = ConfigJson(new { type = "ApiKey", apiKeyName = "X-Api-Key", apiKeyValue = Secret })
        };

        await Service(saved).CreateDataSource(data, CancellationToken.None);

        saved.OfType<DataSource>().Should().ContainSingle();
    }

    [TestCase("Bearer", "\"Token\":\"t-value\",\"Username\":null,\"Password\":null")]
    [TestCase("Basic", "\"Token\":null,\"Username\":\"reader\",\"Password\":\"p-value\"")]
    public async Task CreatingAnApiSource_AsTheWebFormSendsBearerOrBasic_IsSaved(string type, string credentials)
    {
        var saved = new List<object>();
        var connection = $$"""{"BaseUrl":"{{BaseUrl}}","OpenApiSpecUrl":"{{BaseUrl}}/openapi.json","Auth":{"Type":"{{type}}","ApiKeyName":null,"ApiKeyValue":null,"ApiKeyLocation":"header",{{credentials}}},"EndpointFilter":null,"TimeoutSeconds":30}""";
        var data = new DataSourceData { Name = "api", DataSourceType = DataSourceType.Api, ConnectionString = connection };

        await Service(saved).CreateDataSource(data, CancellationToken.None);

        saved.OfType<DataSource>().Should().ContainSingle();
    }

    [Test]
    public async Task ImportingADescription_LogsNeitherItsUrlNorItsQuery()
    {
        using var log = new LogRecorder();
        var specUrl = $"https://specs.example.org/openapi.json?token={Uri.EscapeDataString(Secret)}";
        var clients = Clients(_ => Json("""{"openapi":"3.0.3","info":{"title":"t","version":"1"},"paths":{"/items":{"get":{"responses":{"200":{"description":"ok"}}}}}}"""));
        var service = new OpenApiImportService(clients, log.For<OpenApiImportService>());

        var endpoints = await service.ImportAsync(specUrl, null, CancellationToken.None);

        endpoints.Should().ContainSingle();
        log.Entries.Should().NotBeEmpty();
        log.Entries.Should().OnlyContain(x => !x.FullText.Contains("specs.example.org") && !x.FullText.Contains(TestCredentials.ProviderSecretKey));
    }

    private static ApiConnectionConfig Config(ApiAuthConfig? auth = null, string baseUrl = BaseUrl)
    {
        return new ApiConnectionConfig
        {
            BaseUrl = baseUrl,
            OpenApiSpecUrl = baseUrl + "/openapi.json",
            Auth = auth
        };
    }

    private static ApiQueryDefinition Query(string path, Dictionary<string, string>? headers = null)
    {
        return new ApiQueryDefinition
        {
            Method = "GET",
            Path = path,
            Parameters = new ApiQueryParameters { Header = headers ?? new Dictionary<string, string>() },
            ResultMapping = new ApiResultMapping { ArrayPath = "$.data" }
        };
    }

    private static string ConfigJson(object? auth = null, string specUrl = BaseUrl + "/openapi.json")
    {
        return JsonSerializer.Serialize(new { baseUrl = BaseUrl, openApiSpecUrl = specUrl, auth });
    }

    private static string QueryJson()
    {
        return JsonSerializer.Serialize(new { method = "GET", path = "/items", resultMapping = new { arrayPath = "$.data" } });
    }

    private static DataSource Source(int id = 7)
    {
        return new DataSource
        {
            Id = id,
            Name = "api",
            DataSourceType = DataSourceType.Api,
            EncryptedConnectionData = "stored"
        };
    }

    private static ApiProvider Provider(IHttpClientFactory clients, string configJson, ILogger<ApiProvider>? logger = null)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Decrypt(It.IsAny<string>()))
            .Returns(configJson);

        return new ApiProvider(
            TestTransport.Resolver(encryption.Object),
            clients,
            new OpenApiImportService(clients, NullLogger<OpenApiImportService>.Instance),
            new JsonResponseTabularizer(),
            logger ?? NullLogger<ApiProvider>.Instance);
    }

    private static IHttpClientFactory Clients(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new RespondingHandler(respond)));

        return factory.Object;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static DataSourceService Service(List<object> saved)
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet(new List<DataSource>(), saved)
        };
        var contexts = new Mock<IDbContextFactory<BeaconContext>>();
        contexts
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));
        var encryption = new Mock<IEncryptionService>();
        encryption
            .Setup(x => x.Encrypt(It.IsAny<string>()))
            .Returns("stored");
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(DataSourceType.Api))
            .Returns(Provider(Clients(_ => new HttpResponseMessage(HttpStatusCode.OK)), ConfigJson()));

        return new DataSourceService(
            contexts.Object,
            encryption.Object,
            providers.Object,
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ISchemaGraphService>(),
            new RecordingSecurityAudit(),
            TestTransport.Policy(),
            TestEngineVerifier.Passing,
            NullLogger<DataSourceService>.Instance);
    }

    private sealed class RespondingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
