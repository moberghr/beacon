using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Beacon.Core.Configuration;

namespace Beacon.Tests.Unit;

/// <summary>SC12 — <c>Beacon:Mcp:Audit</c> fails startup validation on a non-positive retention or a malformed header name.</summary>
[TestFixture]
public class McpAuditOptionsValidationTests
{
    private readonly McpDeploymentOptionsValidator _validator = new();

    [Test]
    public void Defaults_AreValid_AndReproduceTodaysBehaviour()
    {
        var options = new McpDeploymentOptions();

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
        options.Audit.Required.Should().BeFalse();
        options.Audit.RetentionDays.Should().BeNull();
        options.Audit.RequestIdHeader.Should().Be("X-Request-Id");
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void NonPositiveRetentionDays_Fails(int days)
    {
        var result = Validate(new McpAuditOptions { RetentionDays = days });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("Beacon:Mcp:Audit:RetentionDays");
    }

    [Test]
    public void PositiveRetentionDays_Succeeds()
    {
        Validate(new McpAuditOptions { RetentionDays = 90 }).Succeeded.Should().BeTrue();
    }

    [TestCase("X Request Id")]
    [TestCase("X-Request-Id:")]
    [TestCase("X-Request-Id\r\nInjected")]
    [TestCase("(bad)")]
    [TestCase("Ünicode")]
    [TestCase(" ")]
    public void MalformedRequestIdHeader_Fails(string header)
    {
        var result = Validate(new McpAuditOptions { RequestIdHeader = header });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("Beacon:Mcp:Audit:RequestIdHeader");
    }

    [TestCase("X-Request-Id")]
    [TestCase("x-amzn-trace-id")]
    [TestCase("X_Correlation.Id~1")]
    public void ValidRequestIdHeader_Succeeds(string header)
    {
        Validate(new McpAuditOptions { RequestIdHeader = header }).Succeeded.Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    public void DisabledRequestIdHeader_Succeeds(string? header)
    {
        Validate(new McpAuditOptions { RequestIdHeader = header }).Succeeded.Should().BeTrue();
    }

    [Test]
    public void BothInvalid_ReportsBothFailures()
    {
        var result = Validate(new McpAuditOptions { RetentionDays = 0, RequestIdHeader = "bad header" });

        result.Failures.Should().HaveCount(2);
    }

    private ValidateOptionsResult Validate(McpAuditOptions audit) =>
        _validator.Validate(null, new McpDeploymentOptions { Audit = audit });
}
