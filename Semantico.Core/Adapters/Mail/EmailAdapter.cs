using System.Reflection;
using Dapper;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Adapters.Mail;

internal class EmailAdapter : IAdapter
{
    private readonly IEmailAdapter _emailAdapter;

    public NotificationType NotificationType => NotificationType.Email;

    public EmailAdapter(IEmailAdapter emailAdapter)
    {
        _emailAdapter = emailAdapter;
    }

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult)
    {
        var to = recipientQueryResult.RecipientDestination;
        var subject = $"[semantico] {recipientQueryResult.QueryResult.ProjectName} - {recipientQueryResult.SubscriptionName}";

        var htmlBody = $@"
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
    <pre>{recipientQueryResult.QueryResult.SqlQuery}</pre>
    <p>Query executed successfully with total records of: {recipientQueryResult.QueryResult.TotalRecords}</p>
    <table>
        <thead>
            <tr>
            {string.Join("", recipientQueryResult.QueryResult.TopRecords.FirstOrDefault().SelectMany(property => $"<th>{property.Key}</th>"))}
            </tr>
        </thead>
        <tbody>
            {string.Join("", recipientQueryResult.QueryResult.TopRecords.Select(record => $"<tr>{string.Join("", record.Select(property => $"<td>{property.Value}</td>"))}</tr>"))}
        </tbody>
    </table>
</body>
</html>";
        await _emailAdapter.SendEmailAsync(to, subject, htmlBody);
    }

    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int lastNotificationResultCount)
    {
        throw new NotSupportedException();
    }
    
    public static Dictionary<string, object> DictionaryFromType(object atype)
    {
        if (atype == null) return new Dictionary<string, object>();
        Type t = atype.GetType();
        PropertyInfo[] props = t.GetProperties();
        Dictionary<string, object> dict = new Dictionary<string, object>();
        foreach (PropertyInfo prp in props)
        {
            object value = prp.GetValue(atype, new object[]{});
            dict.Add(prp.Name, value);
        }
        return dict;
    }
}