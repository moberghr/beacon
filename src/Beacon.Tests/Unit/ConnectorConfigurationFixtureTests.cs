using Beacon.Connector.Api;
using Beacon.Connector.Api.Services;
using Beacon.Connector.BigQuery;
using Beacon.Connector.CloudWatch;
using Beacon.Connector.Databricks;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Pins what the BigQuery, Databricks, CloudWatch and API connectors do with a stored configuration they cannot read:
/// the shape of the failed connection test and of the validation verdict. Nothing is sent to a service, because the
/// configuration is refused first.
/// </summary>
[TestFixture]
public class ConnectorConfigurationFixtureTests
{
    [TestCase("BigQuery", "null", "BeaconException: Failed to parse BigQuery configuration")]
    [TestCase("Databricks", "null", "BeaconException: Failed to parse Databricks configuration")]
    [TestCase("CloudWatch", "null", "BeaconException: Failed to parse CloudWatch configuration")]
    [TestCase("Api", "null", "BeaconException: Failed to parse API connection configuration")]
    [TestCase("BigQuery", "{ not json", "JsonException:")]
    [TestCase("Databricks", "{ not json", "JsonException:")]
    [TestCase("CloudWatch", "{ not json", "BeaconException: Invalid CloudWatch configuration JSON:")]
    [TestCase("Api", "{ not json", "BeaconException: Invalid API configuration JSON:")]
    public async Task TestConnection_WithAnUnreadableConfiguration_FailsWithTheEnginesMessage(string engine, string json, string expectedStart)
    {
        var provider = Provider(engine, json);

        var result = await provider.TestConnectionAsync(Source(engine), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(expectedStart);
        result.ConnectionInfo.Should().BeNullOrEmpty();
        result.TestDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [TestCase("BigQuery")]
    [TestCase("Databricks")]
    public async Task Validate_WithAnEmptyQuery_IsRefusedWithTheEmptyMessage(string engine)
    {
        var provider = Provider(engine, "{}");

        var result = await provider.ValidateQueryAsync(Source(engine), " ", CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("Query cannot be empty");
        result.Warnings.Should().BeEmpty();
        result.Suggestions.Should().BeEmpty();
    }

    [Test]
    public async Task Validate_CloudWatch_WithAnUnreadableConfiguration_ReturnsTheMessageAsTheOnlyError()
    {
        var provider = Provider("CloudWatch", "null");

        var result = await provider.ValidateQueryAsync(Source("CloudWatch"), "fields @message", CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Equal("Failed to parse CloudWatch configuration");
    }

    [Test]
    public async Task Validate_CloudWatch_WithoutLogGroups_IsRefusedAndSuggestsAFieldList()
    {
        var provider = Provider("CloudWatch", "{\"Region\":\"eu-west-1\"}");

        var result = await provider.ValidateQueryAsync(Source("CloudWatch"), "filter @message like /x/", CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Equal("No log groups configured for this CloudWatch data source");
        result.Suggestions.Should().HaveCount(2);
    }

    [Test]
    public async Task Validate_Api_WithAMutatingVerb_IsRefusedWithoutWarnings()
    {
        var provider = Provider("Api", "{}");

        var result = await provider.ValidateQueryAsync(
            Source("Api"),
            "{\"method\":\"POST\",\"path\":\"/a\",\"resultMapping\":{\"arrayPath\":\"$\"}}",
            CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("not permitted");
        result.Warnings.Should().BeEmpty();
    }

    private static IDataSourceProvider Provider(string engine, string json)
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionData(It.IsAny<DataSource>()))
            .Returns(json);

        var validator = new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance);
        var clients = new Mock<IHttpClientFactory>();

        return engine switch
        {
            "BigQuery" => new BigQueryProvider(resolver.Object, validator, NullLogger<BigQueryProvider>.Instance, Microsoft.Extensions.Options.Options.Create(new BigQueryOptions())),
            "Databricks" => new DatabricksProvider(clients.Object, resolver.Object, validator, NullLogger<DatabricksProvider>.Instance),
            "CloudWatch" => new CloudWatchProvider(resolver.Object, NullLogger<CloudWatchProvider>.Instance),
            _ => new ApiProvider(
                resolver.Object,
                clients.Object,
                new OpenApiImportService(clients.Object, NullLogger<OpenApiImportService>.Instance),
                new JsonResponseTabularizer(),
                NullLogger<ApiProvider>.Instance)
        };
    }

    private static DataSource Source(string engine) =>
        new()
        {
            Id = 3,
            Name = "source",
            DataSourceType = Enum.Parse<DataSourceType>(engine),
            EncryptedConnectionData = "encrypted"
        };
}
