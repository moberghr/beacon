using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace Semantico.Core.Adapters;

public static class Helpers
{
    public static string GenerateEmailContent(QueryResult queryResult)
    {
        var querySection = queryResult.ShowQuery 
            ? $@"
            <p>Sql Query:</p>
            <pre>{queryResult.SqlQuery}</pre>"
            : "";
            
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
                    {string.Join("", queryResult.TopRecords.FirstOrDefault().SelectMany(property => $"<th>{property.Key}</th>"))}
                    </tr>
                </thead>
                <tbody>
                    {string.Join("", queryResult.TopRecords.Select(record => $"<tr>{string.Join("", record.Select(property => $"<td>{property.Value}</td>"))}</tr>"))}
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
            
        return $@"
        {querySection}

        Total records: *{queryResult.TotalRecords}*

        |{string.Join("|", queryResult.TopRecords.FirstOrDefault().Select(property => property.Key))}|
        {string.Join("\n", queryResult.TopRecords.Select(record => $"|{string.Join("|", record.Select(property =>  Regex.Replace(property.Value?.ToString() ?? "-", @"\t|\n|\r", "")))}|"))}|";
    }
}