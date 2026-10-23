using ClosedXML.Excel;
using Beacon.Core.Adapters;
using Beacon.Core.Data.Enums;
using System.Collections;
using System.Globalization;
using System.Text;

namespace Beacon.Core.Helpers.File;

internal static class ExportProvider
{
    public static async Task<QueryResultFile> GetReport(FileType exportType, List<IDictionary<string, object?>>? data)
    {
        switch (exportType)
        {
            case FileType.Csv:
                var reportCsv = await CsvBuilder.GetReport(data);
                var (contentTypeCsv, filenameCsv) = FileHelper.GetContentTypeAndFilename(FileType.Csv);
                return new QueryResultFile
                {
                    Data = reportCsv,
                    ContentType = contentTypeCsv,
                    Name = filenameCsv
                };
            case FileType.Xlsx:
                var reportXlsx = await XlsxBuilder.GetReport(data);
                var (contentTypeXlsx, filenameXlsx) = FileHelper.GetContentTypeAndFilename(FileType.Xlsx);
                return new QueryResultFile
                {
                    Data = reportXlsx,
                    ContentType = contentTypeXlsx,
                    Name = filenameXlsx
                };
        }

        throw new ArgumentOutOfRangeException("Unkown export type");
    }

    // The XLSX builds a search for the largest fitting prefix may make after the whole file and the headers alone.
    private const int MaxXlsxSearchBuilds = 5;

    /// <summary>
    /// The report of <paramref name="data"/> within <paramref name="maxBytes"/>: a larger one keeps only its longest prefix
    /// of rows that fits, and the file records how many rows it holds of how many (<see cref="QueryResultFile.Truncated"/>).
    /// A CSV keeps exactly as many rows as fit (it counts each row's bytes as it writes it); an XLSX file compresses
    /// unevenly, so its prefix is searched with at most a few builds and can keep somewhat fewer rows than would fit. A
    /// first row larger than the limit on its own leaves the column headers only; headers larger than the limit leave no
    /// file (<see cref="QueryResultFile.Omitted"/>).
    /// </summary>
    public static async Task<QueryResultFile> GetReport(FileType exportType, List<IDictionary<string, object?>>? data, int maxBytes, CancellationToken cancellationToken, bool neutralizeFormulas = true)
    {
        var rows = data ?? [];
        IReadOnlyList<string> columns = rows.FirstOrDefault()?.Keys.ToList() ?? [];

        return exportType switch
        {
            FileType.Csv => await CsvWithin(rows, columns, maxBytes, cancellationToken, neutralizeFormulas),
            FileType.Xlsx => XlsxWithin(rows, columns, maxBytes, cancellationToken, neutralizeFormulas),
            _ => throw new ArgumentOutOfRangeException(nameof(exportType), exportType, "Unknown export type")
        };
    }

    private static async Task<QueryResultFile> CsvWithin(List<IDictionary<string, object?>> rows, IReadOnlyList<string> columns, int maxBytes, CancellationToken cancellationToken, bool neutralizeFormulas)
    {
        var (report, kept) = await CsvBuilder.GetReportWithin(rows, columns, maxBytes, cancellationToken, neutralizeFormulas);

        return report == null
            ? OmittedFile(FileType.Csv, rows.Count)
            : ReportFile(FileType.Csv, report, kept, rows.Count);
    }

    private static QueryResultFile XlsxWithin(List<IDictionary<string, object?>> rows, IReadOnlyList<string> columns, int maxBytes, CancellationToken cancellationToken, bool neutralizeFormulas)
    {
        var whole = XlsxBuilder.GetReport(rows, columns, cancellationToken, neutralizeFormulas);
        if (whole.Length <= maxBytes)
        {
            return ReportFile(FileType.Xlsx, whole, rows.Count, rows.Count);
        }

        var headers = XlsxBuilder.GetReport([], columns, cancellationToken, neutralizeFormulas);
        if (headers.Length > maxBytes)
        {
            return OmittedFile(FileType.Xlsx, rows.Count);
        }

        // The largest fitting prefix lies between lo rows (fits) and hi rows (does not). Probes alternate between the
        // estimate from the sizes at both ends (close when rows are alike) and the midpoint (progress whatever one row
        // weighs, an outlier included).
        var lo = 0;
        var loReport = headers;
        var hi = rows.Count;
        long hiSize = whole.Length;
        for (var build = 0; build < MaxXlsxSearchBuilds && hi - lo > 1; build++)
        {
            var probe = build % 2 == 0
                ? lo + (int)Math.Floor((maxBytes - loReport.Length) / ((double)(hiSize - loReport.Length) / (hi - lo)))
                : lo + (hi - lo) / 2;
            probe = Math.Clamp(probe, lo + 1, hi - 1);
            var report = XlsxBuilder.GetReport(rows.Take(probe).ToList(), columns, cancellationToken, neutralizeFormulas);
            if (report.Length <= maxBytes)
            {
                lo = probe;
                loReport = report;
            }
            else
            {
                hi = probe;
                hiSize = report.Length;
            }
        }

        return ReportFile(FileType.Xlsx, loReport, lo, rows.Count);
    }

    private static QueryResultFile OmittedFile(FileType exportType, int sourceRowCount)
    {
        var (contentType, name) = FileHelper.GetContentTypeAndFilename(exportType);

        return new QueryResultFile
        {
            Data = [],
            Name = name,
            ContentType = contentType,
            RowCount = 0,
            SourceRowCount = sourceRowCount,
            Omitted = true
        };
    }

    private static QueryResultFile ReportFile(FileType exportType, byte[] data, int rowCount, int sourceRowCount)
    {
        var (contentType, name) = FileHelper.GetContentTypeAndFilename(exportType);

        return new QueryResultFile
        {
            Data = data,
            Name = name,
            ContentType = contentType,
            RowCount = rowCount,
            SourceRowCount = sourceRowCount
        };
    }
}

internal static class CsvBuilder
{
    private const string _csvDateTimeFormat = "dd.MM.yyyy";
    private const string _csvDelimiter = ",";
    private const string _newLine = "\r\n";

    public static Task<byte[]> GetReport(List<IDictionary<string, object?>>? data)
    {
        return GetReport(data, data?.FirstOrDefault()?.Keys.ToList() ?? [], CancellationToken.None);
    }

    /// <summary>The CSV of <paramref name="data"/> under the header <paramref name="columns"/> (written also without rows).</summary>
    public static async Task<byte[]> GetReport(List<IDictionary<string, object?>>? data, IReadOnlyList<string> columns, CancellationToken cancellationToken, bool neutralizeFormulas = true)
    {
        var (report, _) = await GetReportWithin(data, columns, long.MaxValue, cancellationToken, neutralizeFormulas);

        return report!;
    }

    /// <summary>
    /// The CSV of the longest prefix of <paramref name="data"/> within <paramref name="maxBytes"/>, and how many rows it
    /// holds: each row's encoded size is counted before it is written, so the file holds exactly the rows that fit. Null
    /// when the header alone is larger.
    /// </summary>
    public static async Task<(byte[]? Report, int Rows)> GetReportWithin(List<IDictionary<string, object?>>? data, IReadOnlyList<string> columns, long maxBytes, CancellationToken cancellationToken, bool neutralizeFormulas = true)
    {
        using var memoryStream = new MemoryStream();
        using var streamWriter = new StreamWriter(memoryStream, Encoding.UTF8) { NewLine = _newLine };

        var preambleBytes = Encoding.UTF8.GetPreamble();
        streamWriter.BaseStream.Write(preambleBytes, 0, preambleBytes.Length);

        // The size so far is what the stream holds (the writer adds the encoding's own preamble on its first flush); each
        // row then adds exactly its encoded bytes.
        await streamWriter.WriteAsync(Record(columns.Select(x => FormulaGuard.Apply(x, neutralizeFormulas))));
        await streamWriter.FlushAsync();
        var size = memoryStream.Length;
        if (size > maxBytes)
        {
            return (null, 0);
        }

        var rows = 0;
        foreach (var item in data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = Record((item?.Select(x => x.Value) ?? []).Select(x => Format(x, neutralizeFormulas)));
            var recordSize = Encoding.UTF8.GetByteCount(record);
            if (size + recordSize > maxBytes)
            {
                break;
            }

            await streamWriter.WriteAsync(record);
            size += recordSize;
            rows++;
        }

        await streamWriter.FlushAsync();

        return (memoryStream.ToArray(), rows);
    }

    private static string Record(IEnumerable<string?> fields)
    {
        return string.Join(_csvDelimiter, fields.Select(Escape)) + _newLine;
    }

    private static string? Format(object? value, bool neutralizeFormulas)
    {
        return value switch
        {
            null => null,
            string x => FormulaGuard.Apply(x, neutralizeFormulas),
            DateTime x => x.ToString(_csvDateTimeFormat, CultureInfo.InvariantCulture),
            byte[] x => "0x" + Convert.ToHexString(x),
            IEnumerable x => FormulaGuard.Apply(string.Join(_csvDelimiter, x.Cast<object?>().Select(y => Format(y, false) ?? string.Empty)), neutralizeFormulas && x.Cast<object?>().Any(y => y is string)),
            IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    private static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        var needsQuotes = field.Contains('"')
            || field.Contains(',')
            || field.Contains('\r')
            || field.Contains('\n')
            || field[0] == ' '
            || field[^1] == ' ';

        return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}

public static class XlsxBuilder
{
    private const string _xlsxDateTimeFormat = "dd.MM.yyyy";

    public static Task<byte[]> GetReport(List<IDictionary<string, object?>>? data)
    {
        return Task.FromResult(data?.Count > 0
            ? GetReport(data, data[0].Keys.ToList(), CancellationToken.None)
            : GetReport([], [], CancellationToken.None));
    }

    /// <summary>The workbook of <paramref name="data"/> under the header <paramref name="columns"/> (written also without rows).</summary>
    internal static byte[] GetReport(List<IDictionary<string, object?>> data, IReadOnlyList<string> columns, CancellationToken cancellationToken, bool neutralizeFormulas = true)
    {
        using var workbook = FileHelper.CreateWorkbook();

        var worksheet = workbook.Worksheets.Add();
        if (columns.Count > 0)
        {
            WriteData(worksheet, columns, data, cancellationToken, neutralizeFormulas);
            worksheet.Columns().AdjustToContents(1, 2);
        }

        using var memoryStream = new MemoryStream();
        workbook.SaveAs(memoryStream);

        return memoryStream.ToArray();
    }

    private static void WriteData(IXLWorksheet worksheet, IReadOnlyList<string> columns, List<IDictionary<string, object?>> data, CancellationToken cancellationToken, bool neutralizeFormulas)
    {
        for (var column = 0; column < columns.Count; column++)
        {
            var header = worksheet.Cell(1, column + 1);
            header.Value = columns[column] ?? "";
            QuotePrefix(header, columns[column], neutralizeFormulas);
        }

        worksheet.Range(1, 1, 1, columns.Count).Style
            .Font.SetFontColor(XLColor.FromTheme(XLThemeColor.Text1))
            .Alignment.SetWrapText(true)
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center)
            .Alignment.SetVertical(XLAlignmentVerticalValues.Center);

        for (var row = 0; row < data.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = data[row]?.Values.ToList() ?? [];

            for (var column = 0; column < values.Count; column++)
            {
                var value = Normalize(values[column]);
                var cell = worksheet.Cell(row + 2, column + 1);
                cell.Value = XLCellValue.FromObject(value, CultureInfo.InvariantCulture);

                // Per cell: a column may mix dates with other values, which must not be shown as dates.
                if (value is DateTime)
                {
                    cell.Style.DateFormat.SetFormat(_xlsxDateTimeFormat);
                }

                if (value is string text)
                {
                    QuotePrefix(cell, text, neutralizeFormulas);
                }
            }
        }
    }

    // A text cell that could read as a formula carries the quote prefix, so a spreadsheet keeps it as text, also when edited.
    private static void QuotePrefix(IXLCell cell, string? text, bool neutralizeFormulas)
    {
        if (neutralizeFormulas && FormulaGuard.StartsLikeFormula(text))
        {
            cell.Style.IncludeQuotePrefix = true;
        }
    }

    private static object? Normalize(object? value)
    {
        return value switch
        {
            DateTimeOffset x => x.DateTime,
            DateOnly x => x.ToDateTime(TimeOnly.MinValue),
            TimeOnly x => x.ToTimeSpan(),
            _ => value
        };
    }
}

internal static partial class FormulaGuard
{
    public static bool StartsLikeFormula(string? text)
    {
        return !string.IsNullOrEmpty(text) && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !IsPlainNumber(text);
    }

    // A signed number such as -12.5 or +385911234567 cannot start a formula, so it is left as it is (no visible quote in a CSV).
    // Anything else after the sign, including a trailing operator or text, is still guarded.
    private static bool IsPlainNumber(string text)
    {
        return text[0] is '+' or '-' && PlainNumber().IsMatch(text);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[+-]([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?\z")]
    private static partial System.Text.RegularExpressions.Regex PlainNumber();

    /// <summary>The text with a leading <c>'</c> when a spreadsheet would read it as a formula (<paramref name="enabled"/>).</summary>
    public static string? Apply(string? text, bool enabled)
    {
        return enabled && StartsLikeFormula(text) ? "'" + text : text;
    }
}
