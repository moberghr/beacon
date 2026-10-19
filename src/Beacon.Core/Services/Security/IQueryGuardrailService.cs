namespace Beacon.Core.Services.Security;

public interface IQueryGuardrailService
{
    QueryValidationResult ValidateQuery(string sql, QueryGuardrailOptions? options = null);
    string ApplyRowLimit(string sql, int maxRows, string? databaseEngine = null);
    bool IsPiiColumn(string columnName, IReadOnlyList<string>? customPatterns = null);
    Dictionary<string, object?> MaskPiiValues(Dictionary<string, object?> row, IEnumerable<string> piiColumns);
}

public class QueryGuardrailOptions
{
    public bool ReadOnly { get; set; } = true;
    public bool DetectPii { get; set; } = true;
    public List<string>? CustomPiiPatterns { get; set; }

    /// <summary>
    /// The target's dialect name (<c>DataSourceSqlDialect.Of</c>). When it is known, the read-only patterns judge the SQL
    /// with its comments removed as that dialect's tokenizer reads them (<see cref="SqlCommentStripper"/>); null judges the
    /// text as written.
    /// </summary>
    public string? Dialect { get; set; }
}

public record QueryValidationResult(bool IsValid, string? Error = null, bool IsReadOnly = true, List<string>? PiiColumns = null);
