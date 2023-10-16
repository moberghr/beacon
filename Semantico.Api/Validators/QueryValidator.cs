using MediatR;

namespace Semantico.Api.Validators
{
    public static class QueryValidator
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

        public static void ContainsFlaggedWords(string sqlQuery)
        {
            foreach (var flaggedWord in _flaggedWords)
            {
                if (sqlQuery.Contains(flaggedWord, StringComparison.InvariantCultureIgnoreCase))
                {
                    throw new Exception("Query contains keywords that are flagged as not allowed.");
                }
            }
        }
    }
}
