using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
        text.Should().NotContain("response size budget");
    }

    [Test]
    public void FormatResultsAsMarkdown_WideCellRows_StopsAtSizeBudget_AndAppendsBudgetNotice()
    {
        // codex PR-11 R4: the markdown table honors the same size budget as the structured payload.
        // 20 rows × ~64KB cells ≈ 1.2MB raw — far past MaxStructuredPayloadBytes (256KB) even though
        // the row COUNT is comfortably inside maxRows.
        var wideCell = new string('x', 65536);
        var rows = Enumerable.Range(1, 20)
            .Select(i =>
                new Dictionary<string, object?>
                {
                    ["id"] = i,
                    ["blob"] = wideCell
                })
            .ToList();

        var text = ToolHelper.FormatResultsAsMarkdown(rows, maxRows: 100);

        text.Should().Contain("_Further rows omitted — response size budget reached._");
        text.Length.Should().BeLessThan(ToolHelper.MaxStructuredPayloadBytes + wideCell.Length,
            "row emission must stop once the budget is reached");
        // Header + separator + at least one data row are always emitted before the budget can trip.
        text.Split('\n').Count(x => x.StartsWith("|")).Should().BeGreaterThan(2);
        text.Split('\n').Count(x => x.StartsWith("|")).Should().BeLessThan(22, "not all 20 rows can fit the budget");
    }

    [Test]
    public void FormatResultsAsMarkdown_MultibyteWideCells_BudgetIsEnforcedInUtf8Bytes()
    {
        // R6-4: the budget is a BYTE limit and multibyte content is ~3-4 bytes per char in UTF-8 —
        // counting UTF-16 chars would emit several times the wire budget. 4096 repeats of a 7-glyph
        // CJK/emoji block ≈ 90KB per cell in UTF-8, so only ~2 rows fit the 256KB budget while a
        // char-counted loop would have emitted ~8 rows (~720KB on the wire).
        var multibyteCell = string.Concat(Enumerable.Repeat("画🔥漢字テスト", 4096));
        var rows = Enumerable.Range(1, 20)
            .Select(x =>
                new Dictionary<string, object?>
                {
                    ["id"] = x,
                    ["blob"] = multibyteCell
                })
            .ToList();

        var text = ToolHelper.FormatResultsAsMarkdown(rows, maxRows: 100);

        text.Should().Contain("_Further rows omitted — response size budget reached._");

        var oneRowSlackBytes = Encoding.UTF8.GetByteCount($"| 1 | {multibyteCell} |\n");
        Encoding.UTF8.GetByteCount(text).Should().BeLessThanOrEqualTo(
            ToolHelper.MaxStructuredPayloadBytes + oneRowSlackBytes,
            "the emitted markdown must stay within the byte budget plus at most one row's slack");
        // Header + separator + at least one data row are always emitted before the budget can trip.
        text.Split('\n').Count(x => x.StartsWith("|")).Should().BeGreaterThan(2);
    }

    [Test]
    public void FormatResultsAsMarkdown_SmallRowSet_NoBudgetNotice_AllRowsEmitted()
    {
        var text = ToolHelper.FormatResultsAsMarkdown(MakeRows(2), maxRows: 100);

        text.Should().NotContain("response size budget");
        // Header + separator + both data rows — nothing dropped for size.
        text.Split('\n').Count(x => x.StartsWith("|")).Should().Be(4);
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

    [Test]
    public void BuildStructuredPayload_TemporalValues_SerializeInvariantIso8601()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var rows = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["created"] = new DateTime(2026, 8, 16, 13, 5, 30, DateTimeKind.Utc),
                    ["offset"] = new DateTimeOffset(2026, 8, 16, 13, 5, 30, TimeSpan.FromHours(2)),
                    ["amount"] = 1234.5d
                }
            };

            var payload = ToolHelper.BuildStructuredPayload(rows).AsObject();
            var row = payload["rows"]!.AsArray()[0]!.AsArray();

            row[0]!.GetValue<string>().Should().Be("2026-08-16T13:05:30.0000000Z");
            row[1]!.GetValue<string>().Should().Be("2026-08-16T13:05:30.0000000+02:00");
            row[2]!.GetValue<double>().Should().Be(1234.5d);
            payload.ToJsonString().Should().NotContain("16.08.2026");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void BuildStructuredPayload_OverByteBudget_TrimsRowsAndFlagsOmission()
    {
        // ~1 KB per row × 1000 rows ≈ 1 MB serialized — far over the 256 KB budget.
        var wideValue = new string('x', 1024);
        var rows = Enumerable.Range(1, 1000)
            .Select(x =>
                new Dictionary<string, object?>
                {
                    ["id"] = x,
                    ["blob"] = wideValue
                })
            .ToList();

        var payload = ToolHelper.BuildStructuredPayload(rows, maxRows: 1000).AsObject();

        payload["rows"]!.AsArray().Count.Should().BeLessThan(1000);
        payload["truncated"]!.GetValue<bool>().Should().BeTrue();
        payload["rows_omitted_for_size"]!.GetValue<bool>().Should().BeTrue();
        payload["row_count"]!.GetValue<int>().Should().Be(1000);
        Encoding.UTF8.GetByteCount(payload.ToJsonString()).Should().BeLessThanOrEqualTo(ToolHelper.MaxStructuredPayloadBytes);
    }

    [Test]
    public void BuildStructuredPayload_UnderByteBudget_HasNoOmissionFlag()
    {
        var payload = ToolHelper.BuildStructuredPayload(MakeRows(3), maxRows: 100).AsObject();

        payload.ContainsKey("rows_omitted_for_size").Should().BeFalse();
        payload["rows"]!.AsArray().Count.Should().Be(3);
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
