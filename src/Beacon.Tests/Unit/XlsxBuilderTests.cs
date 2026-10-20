using ClosedXML.Excel;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Helpers.File;

namespace Beacon.Tests.Unit;

[TestFixture]
public class XlsxBuilderTests
{
    private static readonly DateTime _date = new(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc);

    private static List<IDictionary<string, object?>> SampleRows()
    {
        return
        [
            new Dictionary<string, object?>
            {
                ["Name"] = "alpha with a deliberately long name to widen the column",
                ["Count"] = 7,
                ["Big"] = 9_000_000_000L,
                ["Price"] = 12.5m,
                ["Ratio"] = 0.25d,
                ["Active"] = true,
                ["Created"] = _date,
                ["Note"] = "first"
            },
            new Dictionary<string, object?>
            {
                ["Name"] = "beta",
                ["Count"] = 8,
                ["Big"] = 9_000_000_001L,
                ["Price"] = 0.75m,
                ["Ratio"] = 1.5d,
                ["Active"] = false,
                ["Created"] = _date.AddDays(1),
                ["Note"] = null
            }
        ];
    }

    private static async Task<XLWorkbook> BuildAsync(List<IDictionary<string, object?>>? data)
    {
        var bytes = await XlsxBuilder.GetReport(data);
        return new XLWorkbook(new MemoryStream(bytes));
    }

    [Test]
    public async Task Header_row_has_column_names_in_dictionary_order()
    {
        using var workbook = await BuildAsync(SampleRows());
        var sheet = workbook.Worksheet(1);

        var headers = Enumerable.Range(1, 8)
            .Select(x => sheet.Cell(1, x).GetString())
            .ToList();

        headers.Should().Equal("Name", "Count", "Big", "Price", "Ratio", "Active", "Created", "Note");
        sheet.Cell(1, 9).IsEmpty().Should().BeTrue();
    }

    [Test]
    public async Task Header_row_is_centered_and_wrapped()
    {
        using var workbook = await BuildAsync(SampleRows());
        var header = workbook.Worksheet(1).Cell(1, 1);

        header.Style.Alignment.WrapText.Should().BeTrue();
        header.Style.Alignment.Horizontal.Should().Be(XLAlignmentHorizontalValues.Center);
        header.Style.Alignment.Vertical.Should().Be(XLAlignmentVerticalValues.Center);
    }

    [Test]
    public async Task Cells_keep_their_value_type()
    {
        using var workbook = await BuildAsync(SampleRows());
        var sheet = workbook.Worksheet(1);

        sheet.Cell(2, 1).DataType.Should().Be(XLDataType.Text);
        sheet.Cell(2, 1).GetString().Should().Be("alpha with a deliberately long name to widen the column");

        sheet.Cell(2, 2).DataType.Should().Be(XLDataType.Number);
        sheet.Cell(2, 2).GetDouble().Should().Be(7);

        sheet.Cell(2, 3).DataType.Should().Be(XLDataType.Number);
        sheet.Cell(2, 3).GetDouble().Should().Be(9_000_000_000d);

        sheet.Cell(2, 4).DataType.Should().Be(XLDataType.Number);
        sheet.Cell(2, 4).GetDouble().Should().Be(12.5);

        sheet.Cell(2, 5).DataType.Should().Be(XLDataType.Number);
        sheet.Cell(2, 5).GetDouble().Should().Be(0.25);

        sheet.Cell(2, 6).DataType.Should().Be(XLDataType.Boolean);
        sheet.Cell(2, 6).GetBoolean().Should().BeTrue();
        sheet.Cell(3, 6).GetBoolean().Should().BeFalse();

        sheet.Cell(2, 7).DataType.Should().Be(XLDataType.DateTime);
        sheet.Cell(2, 7).GetDateTime().Should().Be(_date);
        sheet.Cell(3, 7).GetDateTime().Should().Be(_date.AddDays(1));
    }

    [Test]
    public async Task Null_value_leaves_the_cell_empty()
    {
        using var workbook = await BuildAsync(SampleRows());
        var sheet = workbook.Worksheet(1);

        sheet.Cell(2, 8).GetString().Should().Be("first");
        sheet.Cell(3, 8).IsEmpty().Should().BeTrue();
    }

    [Test]
    public async Task DateTime_column_uses_day_month_year_format()
    {
        using var workbook = await BuildAsync(SampleRows());
        var sheet = workbook.Worksheet(1);

        sheet.Cell(2, 7).Style.DateFormat.Format.Should().Be("dd.MM.yyyy");
        sheet.Cell(3, 7).Style.DateFormat.Format.Should().Be("dd.MM.yyyy");
        sheet.Cell(2, 7).GetFormattedString().Should().Be("07.03.2026");
        sheet.Cell(2, 2).Style.DateFormat.Format.Should().NotBe("dd.MM.yyyy");
    }

    [Test]
    public async Task Rows_are_written_in_input_order_below_the_header()
    {
        using var workbook = await BuildAsync(SampleRows());
        var sheet = workbook.Worksheet(1);

        sheet.LastRowUsed()!.RowNumber().Should().Be(3);
        sheet.Cell(2, 1).GetString().Should().Be("alpha with a deliberately long name to widen the column");
        sheet.Cell(3, 1).GetString().Should().Be("beta");
    }

    [Test]
    public async Task Null_data_returns_an_empty_workbook()
    {
        using var workbook = await BuildAsync(null);

        workbook.Worksheets.Should().HaveCount(1);
        workbook.Worksheet(1).IsEmpty().Should().BeTrue();
    }

    [Test]
    public async Task Empty_data_returns_an_empty_workbook()
    {
        using var workbook = await BuildAsync([]);

        workbook.Worksheets.Should().HaveCount(1);
        workbook.Worksheet(1).IsEmpty().Should().BeTrue();
    }

    [Test]
    public async Task Concurrent_builds_share_one_engine_and_produce_identical_sheets()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(x => Task.Run(() => XlsxBuilder.GetReport(SampleRows()))));

        foreach (var bytes in results)
        {
            using var workbook = new XLWorkbook(new MemoryStream(bytes));
            var sheet = workbook.Worksheet(1);

            sheet.Cell(1, 1).GetString().Should().Be("Name");
            sheet.Cell(3, 1).GetString().Should().Be("beta");
            sheet.Column(1).Width.Should().BeGreaterThan(sheet.ColumnWidth);
        }
    }

    [Test]
    public async Task Unusual_value_types_do_not_fail_the_export()
    {
        var id = Guid.NewGuid();
        List<IDictionary<string, object?>> data =
        [
            new Dictionary<string, object?> { ["Id"] = id, ["Bytes"] = new byte[] { 1, 2 }, ["Span"] = TimeSpan.FromMinutes(90) }
        ];

        using var workbook = await BuildAsync(data);

        var sheet = workbook.Worksheet(1);

        sheet.Cell(2, 1).GetString().Should().Be(id.ToString());
        sheet.Cell(2, 2).GetString().Should().Be("System.Byte[]");
        sheet.Cell(2, 3).DataType.Should().Be(XLDataType.TimeSpan);
        sheet.Cell(2, 3).GetTimeSpan().Should().Be(TimeSpan.FromMinutes(90));
    }

    [Test]
    public async Task DateTimeOffset_is_written_as_a_formatted_date_cell()
    {
        List<IDictionary<string, object?>> data = [new Dictionary<string, object?> { ["When"] = new DateTimeOffset(_date) }];

        using var workbook = await BuildAsync(data);
        var cell = workbook.Worksheet(1).Cell(2, 1);

        cell.DataType.Should().Be(XLDataType.DateTime);
        cell.GetDateTime().Should().Be(_date);
        cell.Style.DateFormat.Format.Should().Be("dd.MM.yyyy");
    }

    [Test]
    public async Task DateOnly_is_written_as_a_formatted_date_cell()
    {
        List<IDictionary<string, object?>> data = [new Dictionary<string, object?> { ["Day"] = new DateOnly(2026, 3, 7) }];

        using var workbook = await BuildAsync(data);
        var cell = workbook.Worksheet(1).Cell(2, 1);

        cell.DataType.Should().Be(XLDataType.DateTime);
        cell.GetDateTime().Should().Be(new DateTime(2026, 3, 7));
        cell.GetFormattedString().Should().Be("07.03.2026");
    }

    [Test]
    public async Task TimeSpan_and_TimeOnly_are_written_as_time_span_cells()
    {
        List<IDictionary<string, object?>> data =
        [
            new Dictionary<string, object?> { ["Span"] = TimeSpan.FromMinutes(90), ["Time"] = new TimeOnly(13, 30) }
        ];

        using var workbook = await BuildAsync(data);
        var sheet = workbook.Worksheet(1);

        sheet.Cell(2, 1).DataType.Should().Be(XLDataType.TimeSpan);
        sheet.Cell(2, 1).GetTimeSpan().Should().Be(TimeSpan.FromMinutes(90));
        sheet.Cell(2, 2).DataType.Should().Be(XLDataType.TimeSpan);
        sheet.Cell(2, 2).GetTimeSpan().Should().Be(new TimeSpan(13, 30, 0));
    }

    [Test]
    public async Task Column_with_null_first_row_is_still_typed_and_formatted()
    {
        List<IDictionary<string, object?>> data =
        [
            new Dictionary<string, object?> { ["Num"] = null, ["Date"] = null },
            new Dictionary<string, object?> { ["Num"] = 5, ["Date"] = _date }
        ];

        using var workbook = await BuildAsync(data);
        var sheet = workbook.Worksheet(1);

        sheet.Cell(2, 1).IsEmpty().Should().BeTrue();
        sheet.Cell(3, 1).DataType.Should().Be(XLDataType.Number);
        sheet.Cell(3, 1).GetDouble().Should().Be(5);
        sheet.Cell(3, 2).DataType.Should().Be(XLDataType.DateTime);
        sheet.Cell(3, 2).GetFormattedString().Should().Be("07.03.2026");
    }

    [Test]
    public async Task Number_in_a_column_that_also_holds_dates_is_not_shown_as_a_date()
    {
        List<IDictionary<string, object?>> data =
        [
            new Dictionary<string, object?> { ["Mixed"] = null },
            new Dictionary<string, object?> { ["Mixed"] = 42 },
            new Dictionary<string, object?> { ["Mixed"] = _date }
        ];

        using var workbook = await BuildAsync(data);
        var sheet = workbook.Worksheet(1);

        sheet.Cell(3, 1).DataType.Should().Be(XLDataType.Number);
        sheet.Cell(3, 1).GetFormattedString().Should().Be("42");
        sheet.Cell(4, 1).DataType.Should().Be(XLDataType.DateTime);
        sheet.Cell(4, 1).GetFormattedString().Should().Be("07.03.2026");
    }

    [Test]
    public async Task Every_header_cell_has_theme_text_colour_and_alignment()
    {
        using var workbook = await BuildAsync(SampleRows());
        var sheet = workbook.Worksheet(1);

        foreach (var column in Enumerable.Range(1, 8))
        {
            var style = sheet.Cell(1, column).Style;

            style.Font.FontColor.ThemeColor.Should().Be(XLThemeColor.Text1);
            style.Alignment.WrapText.Should().BeTrue();
            style.Alignment.Horizontal.Should().Be(XLAlignmentHorizontalValues.Center);
            style.Alignment.Vertical.Should().Be(XLAlignmentVerticalValues.Center);
        }
    }
}
