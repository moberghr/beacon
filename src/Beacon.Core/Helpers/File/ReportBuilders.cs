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

    /// <summary>
    /// The report of <paramref name="data"/> within <paramref name="maxBytes"/>: a larger one keeps only its first rows that
    /// fit, and the file records how many rows it holds of how many (<see cref="QueryResultFile.Truncated"/>). The rows to
    /// keep are estimated once from the size of the whole file and of its first row alone, and corrected while the file is
    /// still over (rarely more than once): an XLSX file compresses unevenly, so it can keep a few rows fewer than would fit.
    /// A first row larger than the limit on its own leaves a file with the column headers and no rows.
    /// </summary>
    public static async Task<QueryResultFile> GetReport(FileType exportType, List<IDictionary<string, object?>>? data, int maxBytes, CancellationToken cancellationToken)
    {
        var rows = data ?? [];
        IReadOnlyList<string> columns = rows.FirstOrDefault()?.Keys.ToList() ?? [];
        var report = await Build(exportType, rows, columns, cancellationToken);
        if (report.Length <= maxBytes)
        {
            return ReportFile(exportType, report, rows.Count, rows.Count);
        }

        var firstRow = await Build(exportType, rows.Take(1).ToList(), columns, cancellationToken);
        if (firstRow.Length > maxBytes)
        {
            return ReportFile(exportType, await Build(exportType, [], columns, cancellationToken), 0, rows.Count);
        }

        var kept = rows.Count;
        while (report.Length > maxBytes && kept > 1)
        {
            // The bytes each row past the first adds, from the last build: the rows that fit after the first one.
            var bytesPerRow = (double)(report.Length - firstRow.Length) / (kept - 1);
            var estimate = 1 + (int)Math.Floor((maxBytes - firstRow.Length) / bytesPerRow);
            kept = Math.Clamp(estimate, 1, kept - 1);
            report = kept == 1 ? firstRow : await Build(exportType, rows.Take(kept).ToList(), columns, cancellationToken);
        }

        return ReportFile(exportType, report, kept, rows.Count);
    }

    private static async Task<byte[]> Build(FileType exportType, List<IDictionary<string, object?>> rows, IReadOnlyList<string> columns, CancellationToken cancellationToken)
    {
        return exportType switch
        {
            FileType.Csv => await CsvBuilder.GetReport(rows, columns, cancellationToken),
            FileType.Xlsx => XlsxBuilder.GetReport(rows, columns, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(exportType), exportType, "Unknown export type")
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
    public static async Task<byte[]> GetReport(List<IDictionary<string, object?>>? data, IReadOnlyList<string> columns, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        using var streamWriter = new StreamWriter(memoryStream, Encoding.UTF8) { NewLine = _newLine };

        var preambleBytes = Encoding.UTF8.GetPreamble();
        streamWriter.BaseStream.Write(preambleBytes, 0, preambleBytes.Length);

        await WriteRecordAsync(streamWriter, columns.Select(x => (string?)x));

        foreach (var item in data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRecordAsync(streamWriter, (item?.Select(x => x.Value) ?? []).Select(Format));
        }

        await streamWriter.FlushAsync();

        return memoryStream.ToArray();
    }

    private static async Task WriteRecordAsync(StreamWriter writer, IEnumerable<string?> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first)
            {
                await writer.WriteAsync(_csvDelimiter);
            }

            first = false;
            await writer.WriteAsync(Escape(field));
        }

        await writer.WriteAsync(_newLine);
    }

    private static string? Format(object? value)
    {
        return value switch
        {
            null => null,
            string x => x,
            DateTime x => x.ToString(_csvDateTimeFormat, CultureInfo.InvariantCulture),
            byte[] x => "0x" + Convert.ToHexString(x),
            IEnumerable x => string.Join(_csvDelimiter, x.Cast<object?>().Select(y => Format(y) ?? string.Empty)),
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
    internal static byte[] GetReport(List<IDictionary<string, object?>> data, IReadOnlyList<string> columns, CancellationToken cancellationToken)
    {
        using var workbook = FileHelper.CreateWorkbook();

        var worksheet = workbook.Worksheets.Add();
        if (columns.Count > 0)
        {
            WriteData(worksheet, columns, data, cancellationToken);
            worksheet.Columns().AdjustToContents(1, 2);
        }

        using var memoryStream = new MemoryStream();
        workbook.SaveAs(memoryStream);

        return memoryStream.ToArray();
    }

    private static void WriteData(IXLWorksheet worksheet, IReadOnlyList<string> columns, List<IDictionary<string, object?>> data, CancellationToken cancellationToken)
    {
        for (var column = 0; column < columns.Count; column++)
        {
            worksheet.Cell(1, column + 1).Value = columns[column] ?? "";
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
            }
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
