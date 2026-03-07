namespace Semantico.Core.Services.Security;

public interface IQueryGuardrailService
{
    QueryValidationResult ValidateQuery(string sql, QueryGuardrailOptions? options = null);
    string ApplyRowLimit(string sql, int maxRows, string? databaseEngine = null);
    List<string> DetectPiiColumns(string sql, IEnumerable<string> columnNames);
    Dictionary<string, object?> MaskPiiValues(Dictionary<string, object?> row, IEnumerable<string> piiColumns);
}

public class QueryGuardrailOptions
{
    public bool ReadOnly { get; set; } = true;
    public int MaxRows { get; set; } = 1000;
    public bool DetectPii { get; set; } = true;
    public bool RequireApproval { get; set; } = false;
    public List<string>? CustomPiiPatterns { get; set; }
}

public record QueryValidationResult(bool IsValid, string? Error = null, bool IsReadOnly = true, List<string>? PiiColumns = null);
