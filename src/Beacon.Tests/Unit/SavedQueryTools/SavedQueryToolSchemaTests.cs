using System.Text.Json;
using Beacon.Core.Data.Enums;
using Beacon.MCP.SavedQueries;
using FluentAssertions;
using NUnit.Framework;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.SavedQueryTools;

[TestFixture]
public class SavedQueryToolSchemaTests
{
    private static readonly Beacon.Core.SavedQueries.SavedQueryToolDefinition LoanBook = Tool(
        "loan_book_by_month",
        [Step(1, 10, "SELECT * FROM loans WHERE issued >= {from} AND issued < {to} AND region = {region} AND amount > {min}",
            Parameter("from", ParameterType.DateTime, "First month"),
            Parameter("to", ParameterType.DateTime),
            Parameter("region", ParameterType.String, "Region code"),
            Parameter("min", ParameterType.Number))],
        [1]);

    [Test]
    public void TheInputSchema_ComesFromTheParameters()
    {
        var schema = SavedQueryToolSchema.BuildInputSchema(LoanBook, includeProject: false);

        schema["type"]!.GetValue<string>().Should().Be("object");
        schema["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
        schema["required"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("from", "to", "region", "min");

        var properties = schema["properties"]!.AsObject();
        properties.Select(x => x.Key).Should().Equal("from", "to", "region", "min");
        properties["from"]!["type"]!.GetValue<string>().Should().Be("string");
        properties["from"]!["description"]!.GetValue<string>().Should().StartWith("First month").And.Contain("ISO 8601");
        properties["region"]!["type"]!.GetValue<string>().Should().Be("string");
        properties["region"]!["description"]!.GetValue<string>().Should().Be("Region code");
        properties["min"]!["type"]!.GetValue<string>().Should().Be("number");
        properties.ContainsKey("project_id").Should().BeFalse();
    }

    [Test]
    public void AToolReachableThroughSeveralProjects_OffersProjectId()
    {
        var tool = Tool("shared", [Step(1, 10, "SELECT 1")], [1, 2]);

        var schema = SavedQueryToolSchema.BuildInputSchema(tool, includeProject: true);

        var project = schema["properties"]!["project_id"]!;
        project["type"]!.GetValue<string>().Should().Be("integer");
        project["enum"]!.AsArray().Select(x => x!.GetValue<int>()).Should().Equal(1, 2);
        schema.ContainsKey("required").Should().BeFalse("a parameterless tool has no required arguments");
    }

    [Test]
    public void TheProtocolTool_IsReadOnlyAndClosedWorld()
    {
        var tool = SavedQueryToolSchema.ToProtocolTool(LoanBook, includeProject: false);

        tool.Name.Should().Be("q_loan_book_by_month");
        tool.Annotations!.ReadOnlyHint.Should().BeTrue();
        tool.Annotations.OpenWorldHint.Should().BeFalse();
        tool.Annotations.DestructiveHint.Should().BeFalse();
        tool.Description.Should().Contain("approved version 3");
    }

    [Test]
    public void Arguments_AreConvertedToTypedValues()
    {
        var error = SavedQueryToolSchema.ConvertArguments(
            LoanBook,
            Json("""{ "from": "2026-01-01", "to": "2026-02-01T00:00:00+02:00", "region": "EU", "min": 12.5, "project_id": 1 }"""),
            out var values,
            out var projectId);

        error.Should().BeNull();
        values["from"].Should().Be(new DateTime(2026, 1, 1));
        ((DateTime)values["from"]!).Kind.Should().Be(DateTimeKind.Unspecified);
        values["to"].Should().Be(new DateTime(2026, 1, 31, 22, 0, 0, DateTimeKind.Utc));
        ((DateTime)values["to"]!).Kind.Should().Be(DateTimeKind.Utc);
        values["region"].Should().Be("EU");
        values["min"].Should().Be(12.5m);
        projectId.Should().Be(1);
    }

    [Test]
    public void AnIntegralNumber_BecomesALong_AndANumericStringIsAccepted()
    {
        var tool = Tool("n", [Step(1, 10, "SELECT {n}", Parameter("n", ParameterType.Number))], [1]);

        SavedQueryToolSchema.ConvertArguments(tool, Json("""{ "n": 7 }"""), out var a, out _).Should().BeNull();
        SavedQueryToolSchema.ConvertArguments(tool, Json("""{ "n": "8" }"""), out var b, out _).Should().BeNull();

        a["n"].Should().Be(7L);
        b["n"].Should().Be(8L);
    }

    [TestCase("""{ "to": "2026-02-01", "region": "EU", "min": 1 }""", "Missing required argument 'from'")]
    [TestCase("""{ "from": null, "to": "2026-02-01", "region": "EU", "min": 1 }""", "Missing required argument 'from'")]
    [TestCase("""{ "from": "yesterday", "to": "2026-02-01", "region": "EU", "min": 1 }""", "'from' must be an ISO 8601")]
    [TestCase("""{ "from": "2026-01-01", "to": "2026-02-01", "region": 5, "min": 1 }""", "'region' must be a string")]
    [TestCase("""{ "from": "2026-01-01", "to": "2026-02-01", "region": "EU", "min": "lots" }""", "'min' must be a number")]
    [TestCase("""{ "from": "2026-01-01", "to": "2026-02-01", "region": "EU", "min": true }""", "'min' must be a number")]
    [TestCase("""{ "from": "2026-01-01", "to": "2026-02-01", "region": "EU", "min": 1, "extra": 1 }""", "Unknown argument 'extra'")]
    [TestCase("""{ "from": "2026-01-01", "to": "2026-02-01", "region": "EU", "min": 1, "project_id": "one" }""", "'project_id' must be an integer")]
    public void InvalidArguments_AreRejected(string json, string expected)
    {
        var error = SavedQueryToolSchema.ConvertArguments(LoanBook, Json(json), out _, out _);

        error.Should().Contain(expected);
    }

    [Test]
    public void AnOverlongString_IsRejected()
    {
        var tool = Tool("s", [Step(1, 10, "SELECT {s}", Parameter("s", ParameterType.String))], [1]);
        var args = new Dictionary<string, JsonElement> { ["s"] = JsonSerializer.SerializeToElement(new string('x', SavedQueryToolSchema.MaxStringArgumentLength + 1)) };

        SavedQueryToolSchema.ConvertArguments(tool, args, out _, out _).Should().Contain("longer than");
    }

    private static Dictionary<string, JsonElement> Json(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
}
