using System.Text.Json;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services;

namespace Beacon.MCP.Services;

internal sealed class McpSignalBuilder
{
    private string _tool = "ask";
    private string _question = "";
    private int? _projectId;
    private int? _dataSourceId;
    private int? _userId;
    private string? _intentClassification;
    private string? _routingDecision;
    private string? _generatedSql;
    private List<string>? _tablesUsed;
    private List<string>? _columnsUsed;
    private bool _schemaValidationFailed;
    private string? _schemaValidationError;
    private bool _executionFailed;
    private string? _executionError;
    private bool _dryRunFailed;
    private string? _dryRunError;
    private bool _emptyResultRetryAttempted;
    private bool _retryAttempted;
    private bool _retrySucceeded;
    private string? _correctedSql;
    private int? _resultRowCount;
    private int _executionTimeMs;
    private bool _isSuccessful;
    private List<KnowledgeItemRef> _knowledgeItems = [];
    private string? _callerHash;
    private int? _requestedParentId;

    // The SQL of record for the current ask flow — lets callers read it back without
    // building a throwaway signal entity.
    public string? GeneratedSql => _generatedSql;

    /// <summary>The <c>ask</c> signal id the caller named (<c>signal_id</c>); unverified input, never a link by itself.</summary>
    public int? RequestedParentId => _requestedParentId;

    public McpSignalBuilder SetTool(string tool) { _tool = tool; return this; }
    public McpSignalBuilder SetQuestion(string question) { _question = question; return this; }
    public McpSignalBuilder SetProjectId(int? projectId) { _projectId = projectId; return this; }
    public McpSignalBuilder SetDataSourceId(int? dataSourceId) { _dataSourceId = dataSourceId; return this; }
    public McpSignalBuilder SetUserId(int? userId) { _userId = userId; return this; }

    // The MCP caller's keyed subject hash (IProjectContext.CallerHash, the caller_hash claim the audit row records); null without one.
    public McpSignalBuilder SetCallerHash(string? callerHash)
    {
        _callerHash = string.IsNullOrEmpty(callerHash) ? null : callerHash;
        return this;
    }

    // The ask signal the caller says this SQL comes from. Only a request: McpSignalService resolves the link.
    public McpSignalBuilder SetRequestedParent(int? signalId)
    {
        _requestedParentId = signalId;
        return this;
    }

    public McpSignalBuilder SetIntent(string intent)
    {
        _intentClassification = intent;
        return this;
    }

    public McpSignalBuilder SetRouting(List<(int DataSourceId, string Name, string Reason)> sources)
    {
        _routingDecision = JsonSerializer.Serialize(sources.Select(s => new { s.DataSourceId, s.Name, s.Reason }));
        if (sources.Count == 1)
            _dataSourceId = sources[0].DataSourceId;
        return this;
    }

    public McpSignalBuilder SetGeneratedSql(string sql, List<string>? tables = null)
    {
        _generatedSql = sql;
        _tablesUsed = tables;
        return this;
    }

    public McpSignalBuilder SetColumnsUsed(IReadOnlyList<string> columns)
    {
        _columnsUsed = columns.Count > 0 ? columns.ToList() : null;
        return this;
    }

    public McpSignalBuilder SetSchemaValidationFailed(string error)
    {
        _schemaValidationFailed = true;
        _schemaValidationError = error;
        return this;
    }

    public McpSignalBuilder SetExecutionFailed(string error)
    {
        _executionFailed = true;
        _executionError = error;
        return this;
    }

    public McpSignalBuilder SetDryRunFailed(string error)
    {
        _dryRunFailed = true;
        _dryRunError = error;
        return this;
    }

    public McpSignalBuilder SetEmptyResultRetry()
    {
        _emptyResultRetryAttempted = true;
        return this;
    }

    public McpSignalBuilder SetRetry(string correctedSql, bool succeeded)
    {
        _retryAttempted = true;
        _retrySucceeded = succeeded;
        _correctedSql = correctedSql;
        return this;
    }

    public McpSignalBuilder SetResult(int? rowCount, int executionTimeMs, bool isSuccessful)
    {
        _resultRowCount = rowCount;
        _executionTimeMs = executionTimeMs;
        _isSuccessful = isSuccessful;
        return this;
    }

    // The knowledge items the prompt carried, saved as the signal's context items: one per kind and id, the first rank kept.
    public McpSignalBuilder SetKnowledgeItems(IEnumerable<KnowledgeItemRef> items)
    {
        _knowledgeItems = items
            .DistinctBy(x => (x.Kind, x.ItemId))
            .ToList();

        return this;
    }

    public McpQuerySignal Build()
    {
        return new McpQuerySignal
        {
            Tool = _tool,
            Question = Truncate(_question, 4000) ?? "",
            ProjectId = _projectId,
            DataSourceId = _dataSourceId,
            UserId = _userId,
            IntentClassification = _intentClassification,
            RoutingDecision = _routingDecision,
            GeneratedSql = _generatedSql,
            TablesUsed = _tablesUsed != null ? JsonSerializer.Serialize(_tablesUsed) : null,
            ColumnsUsed = _columnsUsed != null ? JsonSerializer.Serialize(_columnsUsed) : null,
            SchemaValidationFailed = _schemaValidationFailed,
            SchemaValidationError = Truncate(_schemaValidationError, 4000),
            ExecutionFailed = _executionFailed,
            ExecutionError = Truncate(_executionError, 4000),
            DryRunFailed = _dryRunFailed,
            DryRunError = Truncate(_dryRunError, 4000),
            EmptyResultRetryAttempted = _emptyResultRetryAttempted,
            RetryAttempted = _retryAttempted,
            RetrySucceeded = _retrySucceeded,
            CorrectedSql = _correctedSql,
            CandidateSqlHash = GroundingContentHash.OfCandidateSql(null, _correctedSql, _generatedSql),
            ResultRowCount = _resultRowCount,
            ExecutionTimeMs = _executionTimeMs,
            IsSuccessful = _isSuccessful,
            CallerHash = _callerHash,
            // No parent here: the requested parent (RequestedParentId) is unverified input and never sits on the entity.
            // McpSignalService.RecordSignalAsync takes it as its own argument and sets the parent it resolves inside the
            // caller's own project and identity, or none.
            ContextItems = _knowledgeItems
                .Select(x =>
                    new McpSignalContextItem
                    {
                        Kind = x.Kind,
                        ItemId = x.ItemId,
                        Rank = x.Rank
                    })
                .ToList()
        };
    }

    private static string? Truncate(string? value, int maxLength) =>
        value?.Length > maxLength ? value[..maxLength] : value;
}
