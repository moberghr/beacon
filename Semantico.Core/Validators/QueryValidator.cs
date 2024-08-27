using Semantico.Core.Models;
using Semantico.Core.Models.Queries;

namespace Semantico.Core.Validators;

internal static class QueryValidator
{
    private static string[] _flaggedWords = new string[]
    {
        "insert",
        "update",
        "delete",
        "drop",
        "replace",
        "alter"
    };

    public static void CheckForFlaggedWords(string sqlQuery)
    {
        foreach (var flaggedWord in _flaggedWords)
        {
            if (sqlQuery.Contains(flaggedWord, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new SemanticoException("Query contains keywords that are flagged as not allowed.");
            }
        }
    }

    public static void CheckForParameters(string sqlQuery, List<QueryParameterData> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (sqlQuery.Contains(parameter.Placeholder) == false)
            {
                throw new SemanticoException($"Query does not contain defined parameter with placeholder '{parameter.Placeholder}'.");
            }
        }
    }
}
