using System.Text;
using Beacon.Core.Helpers.File;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Reports;

/// <summary>Byte-for-byte output of the CSV report: BOM, delimiter, line ends, quoting and value formats.</summary>
[TestFixture]
public class CsvReportGoldenTests
{
    private enum Level
    {
        Low,
        High,
    }

    [Test]
    public async Task Report_WithAwkwardValues_MatchesTheRecordedBytes()
    {
        var data = new List<IDictionary<string, object?>>
        {
            Row(
                ("Name", "plain"),
                ("Comma,Header", "a,b"),
                ("Quote", "say \"hi\""),
                ("Multi\nLine", "line1\nline2"),
                ("Windows", "x\r\ny"),
                ("Padded", " lead"),
                ("Trailing", "trail "),
                ("Empty", ""),
                ("Nothing", null),
                ("Tab", "a\tb"),
                ("Unicode", "Zagreb č ć š đ ž"),
                ("Semicolon", "a;b")),
            Row(
                ("Name", "numbers"),
                ("Comma,Header", 1234567.891m),
                ("Quote", -0.5d),
                ("Multi\nLine", 1.5e-7d),
                ("Windows", 3.25f),
                ("Padded", 42),
                ("Trailing", long.MaxValue),
                ("Empty", true),
                ("Nothing", false),
                ("Tab", (short)7),
                ("Unicode", 12.0m),
                ("Semicolon", 1e21d)),
            Row(
                ("Name", "dates"),
                ("Comma,Header", new DateTime(2026, 3, 9, 14, 30, 15)),
                ("Quote", new DateTime(1999, 12, 31)),
                ("Multi\nLine", new DateTimeOffset(2026, 3, 9, 14, 30, 15, TimeSpan.FromHours(2))),
                ("Windows", new DateOnly(2026, 3, 9)),
                ("Padded", new TimeOnly(14, 30, 15)),
                ("Trailing", TimeSpan.FromMinutes(90)),
                ("Empty", Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")),
                ("Nothing", Level.High),
                ("Tab", 'c'),
                ("Unicode", (byte)255),
                ("Semicolon", new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc))),
            Row(
                ("Name", "others"),
                ("Comma,Header", new byte[] { 1, 2, 255 }),
                ("Quote", DBNull.Value),
                ("Multi\nLine", double.NaN),
                ("Windows", float.PositiveInfinity),
                ("Padded", DateTime.MinValue),
                ("Trailing", " "),
                ("Empty", "a\rb"),
                ("Nothing", 123456789012345678.123456789m),
                ("Tab", ulong.MaxValue),
                ("Unicode", -0.0d),
                ("Semicolon", "=SUM(A1)")),
        };

        var bytes = await CsvBuilder.GetReport(data);

        Encoding.UTF8.GetString(bytes).Should().Be(Expected);
        bytes.Take(6).Should().Equal(0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF);
    }

    [Test]
    public async Task Report_WithNoRows_IsTheBytesOrderMarksAndAnEmptyHeaderLine()
    {
        (await CsvBuilder.GetReport([])).Should().Equal(0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, 0x0D, 0x0A);
        (await CsvBuilder.GetReport(null)).Should().Equal(0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, 0x0D, 0x0A);
    }

    [Test]
    public async Task Report_WithOnlyHeaders_IsTheHeaderLine()
    {
        var data = new List<IDictionary<string, object?>> { Row(("A", null), ("B", null)) };

        var text = Encoding.UTF8.GetString(await CsvBuilder.GetReport(data));

        text.Should().Be("\uFEFF\uFEFFA,B\r\n,\r\n");
    }

    [Test]
    public async Task Report_WithCollectionCells_WritesEachAsOneField()
    {
        // Deliberately differs from the old CsvHelper output, which split the elements of an array into extra fields
        // and shifted the columns after it: a collection is now one field, its elements formatted like scalar values,
        // joined with a comma and quoted by the usual rule.
        var data = new List<IDictionary<string, object?>>
        {
            Row(
                ("Ints", new[] { 1, 2 }),
                ("Strings", new[] { "a", "b,c" }),
                ("List", new List<int> { 1 }),
                ("Empty", Array.Empty<int>()),
                ("Nulls", new int?[] { 1, null }),
                ("Dates", new[] { new DateTime(2026, 3, 9), new DateTime(1999, 12, 31) }),
                ("Bytes", new byte[] { 1, 2, 255 }),
                ("After", "next")),
        };

        var text = Encoding.UTF8.GetString(await CsvBuilder.GetReport(data));

        text.Should().Be("\uFEFF\uFEFFInts,Strings,List,Empty,Nulls,Dates,Bytes,After\r\n\"1,2\",\"a,b,c\",1,,\"1,\",\"09.03.2026,31.12.1999\",0x0102FF,next\r\n");
    }

    [Test]
    public async Task Report_WithWhitespaceCells_QuotesOnlyALeadingOrTrailingSpace()
    {
        // As the old writer did: only the space character triggers quoting; tabs and non-breaking spaces never do. A leading tab
        // is neutralised like a leading formula character.
        var data = new List<IDictionary<string, object?>>
        {
            Row(
                ("TabOnly", "\t"),
                ("NbspOnly", "\u00a0"),
                ("TwoSpaces", "  "),
                ("TabLead", "\tx"),
                ("TabTrail", "x\t"),
                ("NbspLead", "\u00a0x"),
                ("NbspTrail", "x\u00a0"),
                ("Inner", "a b")),
        };

        var text = Encoding.UTF8.GetString(await CsvBuilder.GetReport(data));

        text.Should().Be("\uFEFF\uFEFFTabOnly,NbspOnly,TwoSpaces,TabLead,TabTrail,NbspLead,NbspTrail,Inner\r\n'\t,\u00a0,\"  \",'\tx,x\t,\u00a0x,x\u00a0,a b\r\n");
    }

    private static IDictionary<string, object?> Row(params (string Key, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>();
        foreach (var (key, value) in cells)
        {
            row[key] = value;
        }

        return row;
    }

    // Two byte order marks and CRLF line ends, as the report has always been written.
    private const string Expected =
        "\uFEFF\uFEFF" +
        "Name,\"Comma,Header\",Quote,\"Multi\nLine\",Windows,Padded,Trailing,Empty,Nothing,Tab,Unicode,Semicolon\r\n" +
        "plain,\"a,b\",\"say \"\"hi\"\"\",\"line1\nline2\",\"x\r\ny\",\" lead\",\"trail \",,,a\tb,Zagreb \u010d \u0107 \u0161 \u0111 \u017e,a;b\r\n" +
        "numbers,1234567.891,-0.5,1.5E-07,3.25,42,9223372036854775807,True,False,7,12.0,1E+21\r\n" +
        "dates,09.03.2026,31.12.1999,03/09/2026 14:30:15 +02:00,03/09/2026,14:30,01:30:00,0f8fad5b-d9cb-469f-a165-70867728950e,High,c,255,09.03.2026\r\n" +
        "others,0x0102FF,,NaN,Infinity,01.01.0001,\" \",\"a\rb\",123456789012345678.123456789,18446744073709551615,-0,'=SUM(A1)\r\n";
}
