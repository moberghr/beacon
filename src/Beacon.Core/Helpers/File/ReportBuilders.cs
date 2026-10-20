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
}

internal static class CsvBuilder
{
    private const string _csvDateTimeFormat = "dd.MM.yyyy";
    private const string _csvDelimiter = ",";
    private const string _newLine = "\r\n";

    public static async Task<byte[]> GetReport(List<IDictionary<string, object?>>? data)
    {
        using var memoryStream = new MemoryStream();
        using var streamWriter = new StreamWriter(memoryStream, Encoding.UTF8) { NewLine = _newLine };

        var preambleBytes = Encoding.UTF8.GetPreamble();
        streamWriter.BaseStream.Write(preambleBytes, 0, preambleBytes.Length);

        await WriteRecordAsync(streamWriter, (data?.FirstOrDefault()?.Select(x => x.Key) ?? []).Select(x => (string?)x));

        foreach (var item in data ?? [])
        {
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
        using var workbook = FileHelper.CreateWorkbook();

        var worksheet = workbook.Worksheets.Add();
        if (data?.Count > 0)
        {
            WriteData(worksheet, data);
            worksheet.Columns().AdjustToContents(1, 2);
        }

        using var memoryStream = new MemoryStream();
        workbook.SaveAs(memoryStream);

        return Task.FromResult(memoryStream.ToArray());
    }

    private static void WriteData(IXLWorksheet worksheet, List<IDictionary<string, object?>> data)
    {
        var columns = data[0].Keys.ToList();

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
