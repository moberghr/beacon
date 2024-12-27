namespace Semantico.Core.Adapters;

public static class Helpers
{
    public static string GenerateHtmlBody(QueryResult queryResult)
    {
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
            <p>Sql Query:</p>
            <pre>{queryResult.SqlQuery}</pre>
            <p>Query executed successfully with total records of: {queryResult.TotalRecords}</p>
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
}