using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Connector.Api.Services;
using Beacon.Core.Models.Providers;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Parses small OpenAPI 3.0 and 3.1 descriptions, in JSON and in YAML, through <see cref="OpenApiImportService"/> with
/// the OpenAPI library version every host resolves, and pins the endpoint metadata the import produces.
/// </summary>
[TestFixture]
public class OpenApiImportServiceTests
{
    private const string Json30 = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Pets", "version": "1.0.0" },
          "paths": {
            "/pets/{petId}": {
              "parameters": [
                { "name": "petId", "in": "path", "required": true, "description": "Pet id", "schema": { "type": "string", "format": "uuid" } }
              ],
              "get": {
                "tags": [ "pets" ],
                "summary": "Get a pet",
                "description": "Returns one pet",
                "parameters": [
                  { "name": "status", "in": "query", "schema": { "type": "string", "enum": [ "available", "sold" ] } },
                  { "name": "limit", "in": "query", "required": true, "schema": { "type": "integer", "format": "int32" } },
                  { "name": "X-Trace", "in": "header", "schema": { "type": "boolean" } }
                ],
                "responses": {
                  "404": { "description": "Missing" },
                  "200": {
                    "description": "The pet",
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } }
                  }
                }
              },
              "post": {
                "summary": "Replace pets",
                "requestBody": {
                  "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } }
                },
                "responses": {
                  "201": {
                    "description": "Created",
                    "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Pet" } } } }
                  }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "Pet": {
                "type": "object",
                "required": [ "id", "name" ],
                "properties": {
                  "id": { "type": "integer", "format": "int64", "description": "Identifier" },
                  "name": { "type": "string" },
                  "tag": { "type": "string", "nullable": true },
                  "weight": { "type": "number", "format": "double", "nullable": true },
                  "status": { "type": "string", "enum": [ "available", "sold" ] },
                  "born": { "type": "string", "format": "date-time" },
                  "tags": { "type": "array", "items": { "type": "string" } },
                  "owner": {
                    "type": "object",
                    "properties": {
                      "name": { "type": "string" },
                      "address": { "type": "object", "properties": { "city": { "type": "string" } } }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string Yaml31 = """
        openapi: 3.1.0
        info:
          title: Pets
          version: 1.0.0
        paths:
          /pets/{petId}:
            parameters:
              - name: petId
                in: path
                required: true
                description: Pet id
                schema:
                  type: string
                  format: uuid
            get:
              tags: [pets]
              summary: Get a pet
              description: Returns one pet
              parameters:
                - name: status
                  in: query
                  schema:
                    type: string
                    enum: [available, sold]
                - name: limit
                  in: query
                  required: true
                  schema:
                    type: integer
                    format: int32
                - name: X-Trace
                  in: header
                  schema:
                    type: boolean
              responses:
                '404':
                  description: Missing
                '200':
                  description: The pet
                  content:
                    application/json:
                      schema:
                        $ref: '#/components/schemas/Pet'
            post:
              summary: Replace pets
              requestBody:
                content:
                  application/json:
                    schema:
                      $ref: '#/components/schemas/Pet'
              responses:
                '201':
                  description: Created
                  content:
                    application/json:
                      schema:
                        type: array
                        items:
                          $ref: '#/components/schemas/Pet'
        components:
          schemas:
            Pet:
              type: object
              required: [id, name]
              properties:
                id:
                  type: integer
                  format: int64
                  description: Identifier
                name:
                  type: string
                tag:
                  type: [string, 'null']
                weight:
                  type: [number, 'null']
                  format: double
                status:
                  type: string
                  enum: [available, sold]
                born:
                  type: string
                  format: date-time
                tags:
                  type: array
                  items:
                    type: string
                owner:
                  type: object
                  properties:
                    name:
                      type: string
                    address:
                      type: object
                      properties:
                        city:
                          type: string
        """;

    private static IEnumerable<TestCaseData> Specs()
    {
        yield return new TestCaseData(Json30).SetName("Import_Json30_ProducesEndpointMetadata");
        yield return new TestCaseData(ToYaml30(Yaml31)).SetName("Import_Yaml30_ProducesEndpointMetadata");
        yield return new TestCaseData(ToJson31(Json30)).SetName("Import_Json31_ProducesEndpointMetadata");
        yield return new TestCaseData(Yaml31).SetName("Import_Yaml31_ProducesEndpointMetadata");
    }

    [TestCaseSource(nameof(Specs))]
    public void ImportFromString_ProducesEndpointMetadata(string spec)
    {
        var service = new OpenApiImportService(Mock.Of<IHttpClientFactory>(), NullLogger<OpenApiImportService>.Instance);

        var endpoints = service.ImportFromString(spec, null);

        AssertPetEndpoints(endpoints);
    }

    [Test]
    public async Task ImportAsync_ReadsYamlServedOverHttp()
    {
        var service = new OpenApiImportService(ClientsServing(Yaml31, "application/yaml"), NullLogger<OpenApiImportService>.Instance);

        var endpoints = await service.ImportAsync("https://specs.example.test/openapi.yaml", null, CancellationToken.None);

        AssertPetEndpoints(endpoints);
    }

    [Test]
    public async Task ImportAsync_ReadsJsonServedOverHttp()
    {
        var service = new OpenApiImportService(ClientsServing(Json30, "application/json"), NullLogger<OpenApiImportService>.Instance);

        var endpoints = await service.ImportAsync("https://specs.example.test/openapi.json", null, CancellationToken.None);

        AssertPetEndpoints(endpoints);
    }

    [Test]
    public void ImportFromString_AppliesThePathFilter()
    {
        var service = new OpenApiImportService(Mock.Of<IHttpClientFactory>(), NullLogger<OpenApiImportService>.Instance);

        var endpoints = service.ImportFromString(Json30, new Beacon.Connector.Api.Models.ApiEndpointFilter { ExcludePathPatterns = ["/pets/**"] });

        endpoints.Should().BeEmpty();
    }

    [Test]
    public void ImportFromString_ReaderErrors_AreLoggedWithoutTheSpecContent()
    {
        const string marker = "zz-spec-marker";
        var spec = $$"""
            {
              "openapi": "3.0.3",
              "info": { "title": "{{marker}}" },
              "paths": {
                "{{marker}}": { "get": { "responses": { "200": { "description": "ok" } } } },
                "/pets": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "type": "object", "properties": { "id": { "type": "integer" } } } } }
                      }
                    }
                  }
                }
              }
            }
            """;
        var log = new LogRecorder();
        var service = new OpenApiImportService(Mock.Of<IHttpClientFactory>(), log.For<OpenApiImportService>());

        var endpoints = service.ImportFromString(spec, null);

        endpoints.Should().ContainSingle()
            .Which.Path.Should().Be("/pets", "a path key without a leading slash is a reader error, the valid path still imports");
        log.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message.Contains("errors"));
        log.Contains(marker).Should().BeFalse("the reader's messages quote the description, which never reaches a log");
    }

    private const string Swagger20 = """
        {
          "swagger": "2.0",
          "info": { "title": "Pets", "version": "1.0.0" },
          "produces": [ "application/json" ],
          "consumes": [ "application/json" ],
          "paths": {
            "/pets": {
              "post": {
                "tags": [ "pets" ],
                "summary": "Add a pet",
                "parameters": [
                  { "name": "body", "in": "body", "required": true, "schema": { "$ref": "#/definitions/Pet" } },
                  { "name": "dryRun", "in": "query", "type": "boolean" }
                ],
                "responses": {
                  "200": { "description": "The pet", "schema": { "$ref": "#/definitions/Pet" } }
                }
              }
            }
          },
          "definitions": {
            "Pet": {
              "type": "object",
              "properties": {
                "id": { "type": "integer", "format": "int64" },
                "name": { "type": "string" }
              }
            }
          }
        }
        """;

    [Test]
    public void ImportFromString_Swagger20_ImportsTheOperationItsQueryParameterAndTheReferencedResponse()
    {
        var service = new OpenApiImportService(Mock.Of<IHttpClientFactory>(), NullLogger<OpenApiImportService>.Instance);

        var endpoints = service.ImportFromString(Swagger20, null);

        var endpoint = endpoints.Should().ContainSingle().Subject;
        endpoint.Method.Should().Be("POST");
        endpoint.Path.Should().Be("/pets");
        endpoint.Tag.Should().Be("pets");
        endpoint.Parameters.Should().BeEquivalentTo(
            new[] { new ApiParameterMetadata { Name = "dryRun", In = "query", Type = "boolean", Required = false } },
            "the body parameter becomes the request body, not a parameter");
        endpoint.ResponseFields.Should().BeEquivalentTo(
            new ApiResponseFieldMetadata[] { new() { Name = "id", Type = "number" }, new() { Name = "name", Type = "string" } },
            x => x.WithStrictOrdering());
    }

    [Test]
    public void ImportFromString_AnExternalReference_IsNeverFetched()
    {
        var spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Pets", "version": "1.0.0" },
              "paths": {
                "/pets": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "https://example.invalid/x.json#/S" } } }
                      }
                    }
                  }
                }
              }
            }
            """;
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var service = new OpenApiImportService(clients.Object, NullLogger<OpenApiImportService>.Instance);

        var endpoints = service.ImportFromString(spec, null);

        endpoints.Should().ContainSingle()
            .Which.ResponseFields.Should().BeEmpty("the referenced schema stays unloaded");
        clients.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ImportAsync_AnExternalReference_NeverReachesTheNetwork()
    {
        var spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Pets", "version": "1.0.0" },
              "paths": {
                "/pets": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "$ref": "https://example.invalid/x.json#/S" } } }
                      }
                    }
                  }
                }
              }
            }
            """;
        var requested = new List<Uri?>();
        var clients = new Mock<IHttpClientFactory>();
        clients
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new RecordingHandler(spec, requested)));
        var service = new OpenApiImportService(clients.Object, NullLogger<OpenApiImportService>.Instance);

        var endpoints = await service.ImportAsync("https://specs.example.test/openapi.json", null, CancellationToken.None);

        endpoints.Should().ContainSingle();
        requested.Should().ContainSingle().Which!.Host.Should().Be("specs.example.test", "no other document is fetched");
    }

    [Test]
    public void ImportFromString_WithoutPaths_ImportsNothing()
    {
        var service = new OpenApiImportService(Mock.Of<IHttpClientFactory>(), NullLogger<OpenApiImportService>.Instance);

        var endpoints = service.ImportFromString("""{ "openapi": "3.0.3", "info": { "title": "Pets", "version": "1.0.0" } }""", null);

        endpoints.Should().BeEmpty();
    }

    [Test]
    public void ImportFromString_ComposedResponseSchemas_ProduceWhatTheImportReadsToday()
    {
        var spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Pets", "version": "1.0.0" },
              "paths": {
                "/all": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "allOf": [
                          { "type": "object", "properties": { "id": { "type": "integer" } } },
                          { "type": "object", "properties": { "name": { "type": "string" } } }
                        ] } } }
                      }
                    }
                  }
                },
                "/one": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": { "application/json": { "schema": { "oneOf": [
                          { "type": "object", "properties": { "id": { "type": "integer" } } },
                          { "type": "object", "properties": { "name": { "type": "string" } } }
                        ] } } }
                      }
                    }
                  }
                }
              }
            }
            """;
        var service = new OpenApiImportService(Mock.Of<IHttpClientFactory>(), NullLogger<OpenApiImportService>.Instance);

        var endpoints = service.ImportFromString(spec, null);

        endpoints.Single(x => x.Path == "/all").ResponseFields.Should().BeEmpty("fields of an allOf member are not flattened");
        endpoints.Single(x => x.Path == "/one").ResponseFields.Should().BeEmpty("fields of a oneOf member are not flattened");
    }

    [Test]
    public async Task ImportAsync_AnHttpErrorStatus_Throws()
    {
        var clients = new Mock<IHttpClientFactory>();
        clients
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new RecordingHandler("missing", [], HttpStatusCode.NotFound)));
        var service = new OpenApiImportService(clients.Object, NullLogger<OpenApiImportService>.Instance);

        var act = () => service.ImportAsync("https://specs.example.test/openapi.json", null, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Test]
    public void ImportFromString_ATagUsedButNotDeclared_KeepsItsName()
    {
        var spec = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Pets", "version": "1.0.0" },
              "tags": [ { "name": "declared" } ],
              "paths": {
                "/pets": { "get": { "tags": [ "undeclared" ], "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;
        var service = new OpenApiImportService(Mock.Of<IHttpClientFactory>(), NullLogger<OpenApiImportService>.Instance);

        var endpoints = service.ImportFromString(spec, null);

        endpoints.Should().ContainSingle().Which.Tag.Should().Be("undeclared");
    }

    private static void AssertPetEndpoints(List<ApiEndpointMetadata> endpoints)
    {
        endpoints.Select(x => x.Method).Should().BeEquivalentTo("GET", "POST");

        var get = endpoints
            .Where(x => x.Method == "GET")
            .First();
        get.Path.Should().Be("/pets/{petId}");
        get.Tag.Should().Be("pets");
        get.Summary.Should().Be("Get a pet");
        get.Description.Should().Be("Returns one pet");
        get.Parameters.Should().BeEquivalentTo(
            new[]
            {
                new ApiParameterMetadata { Name = "petId", In = "path", Type = "uuid", Required = true, Description = "Pet id" },
                new ApiParameterMetadata { Name = "status", In = "query", Type = "string", Required = false },
                new ApiParameterMetadata { Name = "limit", In = "query", Type = "number", Required = true },
                new ApiParameterMetadata { Name = "X-Trace", In = "header", Type = "boolean", Required = false }
            },
            x => x.WithStrictOrdering());
        get.ResponseFields.Should().BeEquivalentTo(PetFields(), x => x.WithStrictOrdering());

        var post = endpoints
            .Where(x => x.Method == "POST")
            .First();
        post.Path.Should().Be("/pets/{petId}");
        post.Tag.Should().BeNull();
        post.Summary.Should().Be("Replace pets");
        post.Parameters.Should().ContainSingle()
            .Which.Name.Should().Be("petId");
        post.ResponseFields.Should().BeEquivalentTo(PetFields(), x => x.WithStrictOrdering(), "an array response is flattened through its items");
    }

    private static ApiResponseFieldMetadata[] PetFields() =>
    [
        new() { Name = "id", Type = "number", Description = "Identifier" },
        new() { Name = "name", Type = "string" },
        new() { Name = "tag", Type = "string" },
        new() { Name = "weight", Type = "number" },
        new() { Name = "status", Type = "string" },
        new() { Name = "born", Type = "date-time" },
        new() { Name = "tags", Type = "array" },
        new() { Name = "owner.name", Type = "string" },
        new() { Name = "owner.address.city", Type = "string" }
    ];

    private static string ToYaml30(string yaml31) =>
        yaml31
            .Replace("openapi: 3.1.0", "openapi: 3.0.3")
            .Replace("type: [string, 'null']", "type: string\n          nullable: true")
            .Replace("type: [number, 'null']", "type: number\n          nullable: true");

    private static string ToJson31(string json30) =>
        json30
            .Replace("\"3.0.3\"", "\"3.1.0\"")
            .Replace("\"type\": \"string\", \"nullable\": true", "\"type\": [ \"string\", \"null\" ]")
            .Replace("\"type\": \"number\", \"format\": \"double\", \"nullable\": true", "\"type\": [ \"number\", \"null\" ], \"format\": \"double\"");

    private static IHttpClientFactory ClientsServing(string body, string mediaType)
    {
        var clients = new Mock<IHttpClientFactory>();
        clients
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StubHandler(body, mediaType)));
        return clients.Object;
    }

    private sealed class StubHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
    }

    private sealed class RecordingHandler(string body, List<Uri?> requested, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.Add(request.RequestUri);

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
