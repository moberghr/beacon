using Beacon.Core.Services;

namespace Beacon.MCP.Services;

internal static class SqlParsingHelper
{
    // Keeps the text when it holds no SQL: the SQL gate refuses it, the caller does not throw here.
    internal static string CleanSqlResponse(string content)
    {
        return SqlResponseCleaner.Clean(content, out _);
    }
}
