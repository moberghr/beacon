using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using NUnit.Framework;
using Beacon.MCP.Tools;

namespace Beacon.Tests.Unit;

[TestFixture]
public class McpStructuredOutputTests
{
    [Test]
    public void BuildStructuredPayload_ShapesColumnsRowsAndRowCount()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Widget", ["qty"] = 3, ["price"] = 4.5, ["active"] = true, ["note"] = null },
            new() { ["name"] = "Gadget", ["qty"] = 7L, ["price"] = 8.25m, ["active"] = false, ["note"] = "backordered" }
        };

        var payload = ToolHelper.BuildStructuredPayload(rows, maxRows: 100).AsObject();

        payload["columns"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .Should().ContainInOrder("name", "qty", "price", "active", "note");

        var rowsNode = payload["rows"]!.AsArray();
        rowsNode.Should().HaveCount(2);

        var first = rowsNode[0]!.AsArray();
        first[0]!.GetValue<string>().Should().Be("Widget");
        first[1]!.GetValue<int>().Should().Be(3);
        first[2]!.GetValue<double>().Should().Be(4.5);
        first[3]!.GetValue<bool>().Should().BeTrue();
        first[4].Should().BeNull();

        var second = rowsNode[1]!.AsArray();
        second[1]!.GetValue<long>().Should().Be(7L);
        second[2]!.GetValue<decimal>().Should().Be(8.25m);
        second[4]!.GetValue<string>().Should().Be("backordered");

        payload["row_count"]!.GetValue<int>().Should().Be(2);
        payload["truncated"]!.GetValue<bool>().Should().BeFalse();
    }

    [Test]
    public void BuildStructuredPayload_NonPrimitiveValues_SerializeViaToString()
    {
        var guid = Guid.NewGuid();
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = guid }
        };

        var payload = ToolHelper.BuildStructuredPayload(rows).AsObject();

        payload["rows"]![0]![0]!.GetValue<string>().Should().Be(guid.ToString());
    }

    [Test]
    public void BuildStructuredPayload_RowsAboveMaxRows_TruncatesRowsAndFlagsTruncated()
    {
        var payload = ToolHelper.BuildStructuredPayload(MakeRows(5), maxRows: 3).AsObject();

        payload["rows"]!.AsArray().Should().HaveCount(3);
        payload["row_count"]!.GetValue<int>().Should().Be(5);
        payload["truncated"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void BuildStructuredPayload_RowsAtExactCap_FlagsTruncated()
    {
        var payload = ToolHelper.BuildStructuredPayload(MakeRows(3), maxRows: 3).AsObject();

        payload["rows"]!.AsArray().Should().HaveCount(3);
        payload["row_count"]!.GetValue<int>().Should().Be(3);
        payload["truncated"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void BuildStructuredPayload_RowsBelowCap_NotTruncated()
    {
        var payload = ToolHelper.BuildStructuredPayload(MakeRows(2), maxRows: 3).AsObject();

        payload["rows"]!.AsArray().Should().HaveCount(2);
        payload["truncated"]!.GetValue<bool>().Should().BeFalse();
    }

    [Test]
    public void BuildStructuredPayload_InterfaceDictionaryOverload_ProducesSameShape()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["a"] = 1, ["b"] = "x" }
        };

        var payload = ToolHelper.BuildStructuredPayload(rows).AsObject();

        payload["columns"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .Should().ContainInOrder("a", "b");
        payload["rows"]![0]![1]!.GetValue<string>().Should().Be("x");
        payload["row_count"]!.GetValue<int>().Should().Be(1);
    }

    [Test]
    public void FormatResultsAsMarkdown_RowsAboveMaxRows_AppendsTruncationNotice()
    {
        var text = ToolHelper.FormatResultsAsMarkdown(MakeRows(5), maxRows: 3);

        text.Should().Contain("_Showing 3 of 5 rows (truncated). Narrow the query or raise max_rows._");
        // Only maxRows table rows are rendered: header + separator + 3 data rows.
        text.Split('\n').Count(x => x.StartsWith("|")).Should().Be(5);
    }

    [Test]
    public void FormatResultsAsMarkdown_RowsAtExactCap_AppendsRowCapNotice()
    {
        var text = ToolHelper.FormatResultsAsMarkdown(MakeRows(3), maxRows: 3);

        text.Should().Contain("_Row cap of 3 reached — the result set may be truncated._");
        text.Should().NotContain("Narrow the query");
    }

    [Test]
    public void FormatResultsAsMarkdown_RowsBelowCap_HasNoNotice()
    {
        var text = ToolHelper.FormatResultsAsMarkdown(MakeRows(2), maxRows: 3);

        text.Should().NotContain("truncated");
        text.Should().NotContain("Row cap");
    }

    [Test]
    public void Success_WithStructuredNode_SetsStructuredContent()
    {
        var node = new JsonObject { ["signal_id"] = 42 };

        var result = ToolHelper.Success("answer text", node);

        result.StructuredContent.Should().NotBeNull();
        result.StructuredContent!.Value.GetProperty("signal_id").GetInt32().Should().Be(42);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("answer text");
        (result.IsError ?? false).Should().BeFalse();
    }

    [Test]
    public void Success_WithNullStructured_LeavesStructuredContentNull()
    {
        ToolHelper.Success("plain", null).StructuredContent.Should().BeNull();
        ToolHelper.Success("plain").StructuredContent.Should().BeNull();
    }

    private static List<Dictionary<string, object?>> MakeRows(int count)
    {
        return Enumerable.Range(1, count)
            .Select(x =>
                new Dictionary<string, object?>
                {
                    ["id"] = x,
                    ["label"] = $"row-{x}"
                })
            .ToList();
    }
}
