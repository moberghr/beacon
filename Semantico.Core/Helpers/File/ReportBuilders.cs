using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Semantico.Core.Adapters;
using Semantico.Core.Data.Enums;
using System.Data;
using System.Globalization;
using System.Text;

namespace Semantico.Core.Helpers.File;

internal static class ExportProvider
{
    public static async Task<QueryResultFile?> GetReport(FileType? exportType, List<object> data)
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
            case null:
                return null;
        }

        throw new ArgumentOutOfRangeException("Unkown export type");
    }
}

internal static class CsvBuilder
{
    private const string _csvDateTimeFormat = "dd.MM.yyyy";
    private const string _csvDelimiter = ",";
    private const string _newLine = "\r\n";

    public static async Task<byte[]> GetReport(List<object> data)
    {
        var csvConfiguration = new CsvConfiguration(new CultureInfo("en-US"))
        {
            Delimiter = _csvDelimiter,
            NewLine = _newLine
        };

        using var memoryStream = new MemoryStream();
        using var streamWriter = new StreamWriter(memoryStream, Encoding.UTF8);

        var preambleBytes = Encoding.UTF8.GetPreamble();
        streamWriter.BaseStream.Write(preambleBytes, 0, preambleBytes.Length);

        using var csvWriter = new CsvWriter(streamWriter, csvConfiguration);

        csvWriter.Context.TypeConverterOptionsCache.GetOptions<DateTime>().Formats = new[] { _csvDateTimeFormat };

        var columns = ObjectHelpers.GetPropertyNames(data);

        foreach (var col in columns)
        {
            csvWriter.WriteField(col);
        }

        await csvWriter.NextRecordAsync();

        foreach (var item in data)
        {
            var values = columns
                .Select(x => ObjectHelpers.GetPropertyValue(item!, x, "-"))
                .ToArray();

            foreach (var value in values)
            {
                csvWriter.WriteField(value);
            }

            await csvWriter.NextRecordAsync();
        }

        await streamWriter.FlushAsync();

        return memoryStream.ToArray();
    }
}

public static class XlsxBuilder
{
    private const string _xlsxDateTimeFormat = "dd.MM.yyyy";

    public static Task<byte[]> GetReport(List<object> data)
    {
        var xlsxDataTable = PrepareDataTable(data);

        using var workbook = FileHelper.CreateWorkbook();

        var worksheet = workbook.Worksheets.Add();
        worksheet.InsertData(xlsxDataTable);
        worksheet.Columns().AdjustToContents(1, 2);

        using var memoryStream = new MemoryStream();
        workbook.SaveAs(memoryStream);
        memoryStream.Seek(0, SeekOrigin.Begin);

        return Task.FromResult(memoryStream.ToArray());
    }

    private static DataTable PrepareDataTable(List<object> data)
    {
        var dt = new DataTable();

        if (data.Count == 0)
        {
            return dt;
        }

        var type = data.First().GetType();

        var columns = ObjectHelpers.GetPropertyNames(data);

        foreach (var column in columns)
        {
            var columnName = column;
            // check if column name is unique as DataTable doesn't allow duplicates
            var index = 1;
            while (dt.Columns.Contains(columnName))
            {
                columnName = column + index.ToString();
                index++;
            }

            var propertyType = ObjectHelpers.GetPropertyType(type, columnName);

            if (propertyType == null)
            {
                dt.Columns.Add(columnName, typeof(string));
                continue;
            }

            dt.Columns.Add(columnName, Nullable.GetUnderlyingType(propertyType) ?? propertyType);
        }

        foreach (var item in data)
        {
            var values = columns
                .Select(x => ObjectHelpers.GetPropertyValue(item!, x))
                .ToArray();

            dt.Rows.Add(values);
        }

        return dt;
    }

    private static IXLWorksheet InsertData(this IXLWorksheet worksheet, DataTable dt)
    {
        var cell = worksheet.Cell(1, 1);

        var table = cell.InsertTable(dt, createTable: false);

        var header = table.HeadersRow();

        header.Style
            .Font.SetFontColor(XLColor.FromTheme(XLThemeColor.Text1))
            .Alignment.SetWrapText(true)
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center)
            .Alignment.SetVertical(XLAlignmentVerticalValues.Center);

        var firstRow = table.FirstRowUsed().RowNumber() + 1;
        var lastRow = table.LastRowUsed().RowNumber();

        foreach (DataColumn column in dt.Columns)
        {
            var columnStyle = worksheet.Range(firstRow, column.Ordinal + 1, lastRow, column.Ordinal + 1).Style;

            if (column.DataType == typeof(DateTime))
            {
                columnStyle.DateFormat.SetFormat(_xlsxDateTimeFormat);
                continue;
            }
        }

        return worksheet;
    }
}