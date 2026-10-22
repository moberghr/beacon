using System.Text;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Mail;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers.File;
using ClosedXML.Excel;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Reports;

/// <summary>
/// A subscription's attachment stays within <c>Beacon:Subscriptions:MaxAttachmentBytes</c>: an export that would be larger
/// keeps only its first rows that fit, the file says how many rows it holds of how many, and the e-mail that carries it
/// says so in terms of the rows the message delivers. A first row larger than the limit alone leaves the column headers
/// only. An export that fits is sent whole, without a note.
/// </summary>
[TestFixture]
public class AttachmentSizeCapTests
{
    private const int Cap = 64 * 1024;
    private const int FirstId = 100_001;

    private static readonly Random Letters = new(7);

    private static IEnumerable<FileType> FileTypes => Enum.GetValues<FileType>();

    [TestCaseSource(nameof(FileTypes))]
    public async Task AnExportAboveTheCap_KeepsTheFirstRowsThatFit(FileType type)
    {
        var rows = Rows(20_000);

        var file = await ExportProvider.GetReport(type, rows, Cap, CancellationToken.None);

        file.Data.Length.Should().BeLessThanOrEqualTo(Cap);
        file.SourceRowCount.Should().Be(20_000);
        file.RowCount.Should().BeGreaterThan(0).And.BeLessThan(20_000);
        file.Truncated.Should().BeTrue();
        RowIds(type, file.Data).Should().Equal(Enumerable.Range(FirstId, file.RowCount!.Value), "the file holds the first rows, in order");
    }

    // Rows of one width: the estimate is exact, so one more row would not fit.
    [Test]
    public async Task ACsvAboveTheCap_KeepsAsManyRowsAsFit()
    {
        var rows = Rows(20_000);

        var file = await ExportProvider.GetReport(FileType.Csv, rows, Cap, CancellationToken.None);

        var oneMore = await CsvBuilder.GetReport(rows.Take(file.RowCount!.Value + 1).ToList());
        oneMore.Length.Should().BeGreaterThan(Cap);
    }

    [TestCaseSource(nameof(FileTypes))]
    public async Task AnExportExactlyAtTheCap_IsWhole(FileType type)
    {
        var rows = Rows(500);
        var whole = await ExportProvider.GetReport(type, rows);

        var file = await ExportProvider.GetReport(type, rows, whole.Data.Length, CancellationToken.None);

        file.Truncated.Should().BeFalse();
        file.RowCount.Should().Be(500);
        file.Data.Length.Should().Be(whole.Data.Length);
    }

    [Test]
    public async Task ACsvOneByteOverTheCap_LosesOneRow()
    {
        var rows = Rows(500);
        var whole = await ExportProvider.GetReport(FileType.Csv, rows);

        var file = await ExportProvider.GetReport(FileType.Csv, rows, whole.Data.Length - 1, CancellationToken.None);

        file.RowCount.Should().Be(499);
        file.SourceRowCount.Should().Be(500);
        file.Data.Length.Should().BeLessThan(whole.Data.Length);
    }

    [TestCaseSource(nameof(FileTypes))]
    public async Task AnExportWithinTheCap_IsWhole(FileType type)
    {
        var file = await ExportProvider.GetReport(type, Rows(20), Cap, CancellationToken.None);

        file.RowCount.Should().Be(20);
        file.SourceRowCount.Should().Be(20);
        file.Truncated.Should().BeFalse();
        RowIds(type, file.Data).Should().Equal(Enumerable.Range(FirstId, 20));
    }

    [TestCaseSource(nameof(FileTypes))]
    public async Task AnEmptyOrMissingResult_IsAnEmptyFile(FileType type)
    {
        var empty = await ExportProvider.GetReport(type, [], Cap, CancellationToken.None);
        var missing = await ExportProvider.GetReport(type, null, Cap, CancellationToken.None);

        foreach (var file in new[] { empty, missing })
        {
            file.RowCount.Should().Be(0);
            file.SourceRowCount.Should().Be(0);
            file.Truncated.Should().BeFalse();
            file.Data.Length.Should().BeLessThanOrEqualTo(Cap);
        }
    }

    [TestCaseSource(nameof(FileTypes))]
    public async Task AFirstRowLargerThanTheCap_LeavesTheHeadersOnly(FileType type)
    {
        // Four cells of 30,000 random letters (an XLSX cell holds at most 32,767 characters): over the cap even compressed.
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = FirstId, ["a"] = RandomText(30_000), ["b"] = RandomText(30_000), ["c"] = RandomText(30_000), ["d"] = RandomText(30_000) },
            new Dictionary<string, object?> { ["id"] = FirstId + 1, ["a"] = "small", ["b"] = "small", ["c"] = "small", ["d"] = "small" }
        };

        var file = await ExportProvider.GetReport(type, rows, Cap, CancellationToken.None);

        file.RowCount.Should().Be(0);
        file.SourceRowCount.Should().Be(2);
        file.Truncated.Should().BeTrue();
        file.Data.Length.Should().BeLessThanOrEqualTo(Cap);
        Headers(type, file.Data).Should().Equal("id", "a", "b", "c", "d");
        RowIds(type, file.Data).Should().BeEmpty();
    }

    // One huge last row: the rows before it that fit are kept, not cut in proportion to the outlier.
    [Test]
    public async Task ACsvWithATrailingOutlier_KeepsEveryRowThatFits()
    {
        var rows = Enumerable.Range(FirstId, 9_999)
            .Select(x => (IDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = x, ["note"] = "x" })
            .Append(new Dictionary<string, object?> { ["id"] = FirstId + 9_999, ["note"] = new string('x', 1024 * 1024) })
            .ToList();

        var file = await ExportProvider.GetReport(FileType.Csv, rows, Cap, CancellationToken.None);

        file.Data.Length.Should().BeLessThanOrEqualTo(Cap);
        file.RowCount.Should().BeGreaterThan(6_000);
        (await CsvBuilder.GetReport(rows.Take(file.RowCount!.Value + 1).ToList())).Length.Should().BeGreaterThan(Cap);
        RowIds(FileType.Csv, file.Data).Should().Equal(Enumerable.Range(FirstId, file.RowCount.Value));
    }

    [Test]
    public async Task AnXlsxWithATrailingOutlier_KeepsNearlyEveryRowThatFits()
    {
        var columns = Enumerable.Range(1, 8).Select(x => $"c{x}").ToList();
        var rows = Enumerable.Range(FirstId, 9_999)
            .Select(x => Row(x, columns, _ => null))
            .Append(Row(FirstId + 9_999, columns, _ => RandomText(32_000)))
            .ToList();

        var file = await ExportProvider.GetReport(FileType.Xlsx, rows, Cap, CancellationToken.None);

        var largest = LargestFittingXlsxPrefix(rows);
        file.Data.Length.Should().BeLessThanOrEqualTo(Cap);
        file.RowCount.Should().BeLessThanOrEqualTo(largest).And.BeGreaterThanOrEqualTo((int)(largest * 0.9));
        RowIds(FileType.Xlsx, file.Data).Should().Equal(Enumerable.Range(FirstId, file.RowCount!.Value));
    }

    // Column headers alone over the cap: no file is attached, and the e-mail says why.
    [TestCaseSource(nameof(FileTypes))]
    public async Task HeadersLargerThanTheCap_AttachNothing(FileType type)
    {
        var columns = Enumerable.Range(1, 400).Select(x => $"{x}_{RandomText(400)}").ToList();
        var rows = new List<IDictionary<string, object?>> { Row(FirstId, columns, _ => 1) };

        var file = await ExportProvider.GetReport(type, rows, Cap, CancellationToken.None);

        file.Omitted.Should().BeTrue();
        file.Data.Should().BeEmpty();
        file.RowCount.Should().Be(0);
        file.SourceRowCount.Should().Be(1);
    }

    [Test]
    public async Task AnOmittedAttachment_IsNotSent_AndTheEmailSaysSo()
    {
        var mail = new Mock<IEmailAdapter>();
        string? sentBody = null;
        var attached = true;
        mail
            .Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryResultFile?>()))
            .Callback<string, string, string, QueryResultFile?>((_, _, body, attachment) =>
            {
                sentBody = body;
                attached = attachment != null;
            })
            .Returns(Task.CompletedTask);
        var file = new QueryResultFile { Data = [], Name = "report.csv", ContentType = "text/csv", RowCount = 0, SourceRowCount = 7, Omitted = true };

        await new EmailAdapter(mail.Object).SendNotificationAsync(
            new RecipientQueryResult
            {
                RecipientDestination = "ops@bank.example",
                RecipientNotificationType = NotificationType.Email,
                QueryResult = Result(),
                QueryResultFile = file
            },
            null);

        attached.Should().BeFalse();
        sentBody.Should().Contain("No attachment:").And.Contain("even the column headers of report.csv are larger than the attachment size limit");
    }

    [Test]
    public void TheEmail_SaysTheAttachmentWasTruncated_InTermsOfTheRowsItDelivers()
    {
        var file = File(rowCount: 1_234, sourceRowCount: 5_000, name: "<b>report</b>.csv");

        var body = Helpers.GenerateEmailContent(Result(), file);

        body.Should().Contain("truncated").And.Contain("first 1234 of the 5000 rows this message delivers");
        body.Should().Contain("&lt;b&gt;report&lt;/b&gt;.csv").And.NotContain("<b>report</b>");
    }

    [Test]
    public void TheEmail_SaysTheAttachmentHoldsNoRows_WhenOneRowIsOverTheLimit()
    {
        var body = Helpers.GenerateEmailContent(Result(), File(rowCount: 0, sourceRowCount: 5_000));

        body.Should().Contain("The attachment holds no rows").And.Contain("one row of the 5000 rows this message delivers is larger than the attachment size limit");
    }

    [Test]
    public void TheEmail_HasNoNote_WhenTheAttachmentIsWhole()
    {
        var body = Helpers.GenerateEmailContent(Result(), File(rowCount: 20, sourceRowCount: 20));

        body.Should().NotContain("truncated");
        body.Should().Be(Helpers.GenerateEmailContent(Result()));
    }

    [TestCase(1_234, true)]
    [TestCase(5_000, false)]
    public async Task TheEmailAdapter_SendsTheNote_OnlyWithATruncatedAttachment(int rowCount, bool noted)
    {
        var mail = new Mock<IEmailAdapter>();
        string? sentBody = null;
        QueryResultFile? sentFile = null;
        mail
            .Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<QueryResultFile?>()))
            .Callback<string, string, string, QueryResultFile?>((_, _, body, attachment) =>
            {
                sentBody = body;
                sentFile = attachment;
            })
            .Returns(Task.CompletedTask);
        var file = File(rowCount, 5_000);

        await new EmailAdapter(mail.Object).SendNotificationAsync(
            new RecipientQueryResult
            {
                RecipientDestination = "ops@bank.example",
                RecipientNotificationType = NotificationType.Email,
                QueryResult = Result(),
                QueryResultFile = file
            },
            null);

        sentFile.Should().BeSameAs(file);
        sentBody.Should().NotBeNull();
        sentBody!.Contains("The attachment was truncated").Should().Be(noted);
    }

    private static IDictionary<string, object?> Row(int id, IEnumerable<string> columns, Func<string, object?> value)
    {
        var row = new Dictionary<string, object?> { ["id"] = id };
        foreach (var column in columns)
        {
            row[column] = value(column);
        }

        return row;
    }

    private static int LargestFittingXlsxPrefix(List<IDictionary<string, object?>> rows)
    {
        var columns = rows[0].Keys.ToList();
        var lo = 0;
        var hi = rows.Count;
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            if (XlsxBuilder.GetReport(rows.Take(mid).ToList(), columns, CancellationToken.None).Length <= Cap)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static QueryResultFile File(int rowCount, int sourceRowCount, string name = "report.csv") =>
        new() { Data = [], Name = name, ContentType = "text/csv", RowCount = rowCount, SourceRowCount = sourceRowCount };

    private static QueryResult Result() =>
        new()
        {
            QueryResults = "[]",
            TotalRecords = 9_000,
            DataSourceName = "warehouse",
            SqlQuery = "SELECT 1",
            SubscriptionName = "daily",
            SubscriptionId = 5
        };

    // Rows of one width: six-digit ids and a fixed-length note.
    private static List<IDictionary<string, object?>> Rows(int count)
    {
        return Enumerable.Range(FirstId, count)
            .Select(x => (IDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = x,
                ["note"] = $"row {x} " + new string('x', 40)
            })
            .ToList();
    }

    // Text that does not compress, so it stays large in an XLSX file too.
    private static string RandomText(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)('a' + Letters.Next(26));
        }

        return new string(chars);
    }

    private static List<int> RowIds(FileType type, byte[] data)
    {
        if (type == FileType.Csv)
        {
            return CsvLines(data)
                .Skip(1)
                .Select(x => int.Parse(x.Split(',')[0]))
                .ToList();
        }

        using var stream = new MemoryStream(data);
        using var workbook = new XLWorkbook(stream);
        return workbook.Worksheet(1)
            .RowsUsed()
            .Skip(1)
            .Select(x => (int)x.Cell(1).GetDouble())
            .ToList();
    }

    private static List<string> Headers(FileType type, byte[] data)
    {
        if (type == FileType.Csv)
        {
            return CsvLines(data)
                .First()
                .Split(',')
                .ToList();
        }

        using var stream = new MemoryStream(data);
        using var workbook = new XLWorkbook(stream);
        return workbook.Worksheet(1)
            .Row(1)
            .CellsUsed()
            .Select(x => x.GetString())
            .ToList();
    }

    private static string[] CsvLines(byte[] data)
    {
        return Encoding.UTF8.GetString(data)
            .TrimStart('﻿')
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
