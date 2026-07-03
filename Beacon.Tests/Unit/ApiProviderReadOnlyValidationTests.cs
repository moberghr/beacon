using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Connector.Api;
using Beacon.Connector.Api.Services;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit;

/// <summary>
/// Covers the read-only verb enforcement added to <see cref="ApiProvider.ValidateQueryAsync"/>
/// in B2. Previously ValidateQueryAsync only checked that the required fields were present, so a
/// mutating verb (POST/PUT/DELETE/PATCH) would validate. Now only GET/HEAD/OPTIONS are accepted.
/// </summary>
[TestFixture]
public class ApiProviderReadOnlyValidationTests
{
    private ApiProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var openApiImport = new OpenApiImportService(httpClientFactory.Object, NullLogger<OpenApiImportService>.Instance);
        var tabularizer = new JsonResponseTabularizer();

        _provider = new ApiProvider(
            Mock.Of<IEncryptionService>(),
            httpClientFactory.Object,
            openApiImport,
            tabularizer,
            NullLogger<ApiProvider>.Instance);
    }

    private static DataSource ApiDataSource() =>
        new()
        {
            Name = "api",
            DataSourceType = DataSourceType.Api,
            EncryptedConnectionData = "unused"
        };

    private static string QueryWithMethod(string method) =>
        JsonSerializer.Serialize(new
        {
            method,
            path = "/items",
            resultMapping = new { arrayPath = "$.data" }
        });

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    [TestCase("PATCH")]
    public async Task ValidateQueryAsync_MutatingVerb_IsRejected(string method)
    {
        var result = await _provider.ValidateQueryAsync(ApiDataSource(), QueryWithMethod(method));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(x => x.Contains("read-only", StringComparison.OrdinalIgnoreCase));
    }

    [TestCase("GET")]
    [TestCase("HEAD")]
    [TestCase("OPTIONS")]
    public async Task ValidateQueryAsync_ReadOnlyVerb_IsAccepted(string method)
    {
        var result = await _provider.ValidateQueryAsync(ApiDataSource(), QueryWithMethod(method));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }
}
