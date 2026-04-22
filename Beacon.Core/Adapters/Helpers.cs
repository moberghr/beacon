using System.Text.RegularExpressions;
using Beacon.Core.Adapters.Jira;
using Beacon.Core.Adapters.Shared;

namespace Beacon.Core.Adapters;

public static class Helpers
{
    public static string GenerateEmailContent(QueryResult queryResult)
    {
        var querySection = queryResult.ShowQuery
            ? $@"
            <p>Sql Query:</p>
            <pre>{queryResult.SqlQuery}</pre>"
            : "";

        var firstRecord = queryResult.TopRecords.GetFirstRecordSafe();
        var tableHeaders = firstRecord != null
            ? string.Join("", firstRecord.Select(property => $"<th>{property.Key}</th>"))
            : "<th>No data</th>";

        var tableRows = queryResult.TopRecords.HasRecords()
            ? string.Join("", queryResult.TopRecords.Select(record => $"<tr>{string.Join("", record.Select(property => $"<td>{CellValueFormatter.Format(property.Value)}</td>"))}</tr>"))
            : "<tr><td>No records found</td></tr>";

        return $@"
        <html>
        <head>
            <style>
                table {{
                    font-family: Arial, sans-serif;
                    border-collapse: collapse;
                    width: 100%;
                }}
                th, td {{
                    border: 1px solid #dddddd;
                    text-align: left;
                    padding: 8px;
                }}
                th {{
                    background-color: #f2f2f2;
                }}
            </style>
        </head>
        <body>
            <h2>Query Execution Results</h2>
            {querySection}
            <p>Query executed successfully with total records of: {queryResult.TotalRecords}</p>
            <p>First {queryResult.TopRecords.Count} records:</p>
            <table>
                <thead>
                    <tr>
                    {tableHeaders}
                    </tr>
                </thead>
                <tbody>
                    {tableRows}
                </tbody>
            </table>
        </body>
        </html>";
    }

    public static string GenerateJiraContent(QueryResult queryResult)
    {
        var querySection = queryResult.ShowQuery
            ? $@"
        h3. Sql Query:
        {{code:sql}}
            {queryResult.SqlQuery}
        {{code}}

        ----"
            : "";

        var firstRecord = queryResult.TopRecords.GetFirstRecordSafe();
        if (firstRecord == null)
        {
            return $@"
        {querySection}

        Total records: *{queryResult.TotalRecords}*

        No records to display.";
        }

        var headers = string.Join("|", firstRecord.Select(property => property.Key));
        var rows = string.Join("\n", queryResult.TopRecords.Select(record =>
            $"|{string.Join("|", record.Select(property => CellValueFormatter.FormatAndSanitize(property.Value) ))}|"));

        return $@"
        {querySection}

        Total records: *{queryResult.TotalRecords}*

        |{headers}|
        {rows}";
    }

    /// <summary>
    /// Generates Atlassian Document Format (ADF) for Jira Cloud API v3.
    /// This produces properly formatted rich text with tables, code blocks, etc.
    /// </summary>
    public static AdfDocument GenerateJiraAdf(QueryResult queryResult)
    {
        var builder = new AdfBuilder();

        // Add SQL query section if enabled
        if (queryResult.ShowQuery && !string.IsNullOrEmpty(queryResult.SqlQuery))
        {
            builder
                .AddHeading(3, "SQL Query")
                .AddCodeBlock(queryResult.SqlQuery, "sql")
                .AddRule();
        }

        // Add summary
        builder.AddParagraph(p => p
            .Text("Total records: ")
            .Bold(queryResult.TotalRecords.ToString()));

        // Add results table
        var firstRecord = queryResult.TopRecords.GetFirstRecordSafe();
        if (firstRecord != null && queryResult.TopRecords.HasRecords())
        {
            builder.AddHeading(4, $"First {queryResult.TopRecords.Count} records:");

            var headers = firstRecord.Select(p => p.Key).ToList();
            var rows = queryResult.TopRecords.Select(record =>
                record.Select(p => CellValueFormatter.FormatAndSanitize(p.Value)).ToList()
            ).ToList();

            builder.AddTable(headers, rows);
        }
        else
        {
            builder.AddParagraph("No records to display.");
        }

        return builder.Build();
    }

    /// <summary>
    /// Generates ADF for a simple text comment.
    /// </summary>
    public static AdfDocument GenerateJiraCommentAdf(string comment)
    {
        return new AdfBuilder()
            .AddParagraph(comment)
            .Build();
    }

}