using ClosedXML.Excel;
using ClosedXML.Graphics;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Helpers.File;

internal static class FileHelper
{
    private const string _dateTimeFormat = "yyyyMMddTHHmmss";

    public static (string ContentType, string Filename) GetContentTypeAndFilename(FileType exportType)
    {
        var contentType = exportType switch
        {
            FileType.Csv => "text/csv",
            FileType.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };

        var filename = $"{DateTime.UtcNow.ToString(_dateTimeFormat)}.{exportType.ToString().ToLowerInvariant()}";

        return (contentType, filename);
    }

    public static XLWorkbook CreateWorkbook()
    {
        // we are setting this free default font, as the default for ClosedXml is Calibri which is not present on Linux (failback is Microsoft Sans Serif which is also not present)
        // Liberation Sans is very similar to Calibri
        var loadOptions = new LoadOptions
        {
            GraphicEngine = new DefaultGraphicEngine("Liberation Sans")
        };

        return new XLWorkbook(loadOptions);
    }
}