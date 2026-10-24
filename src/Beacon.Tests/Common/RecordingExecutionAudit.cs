using Beacon.Core.Services;

namespace Beacon.Tests.Common;

/// <summary>An execution log that records each start and end, and appends "start" and "end" to a shared order list.</summary>
internal sealed class RecordingExecutionAudit(List<string>? order = null) : ISqlExecutionAudit
{
    private int _nextId;

    public List<SqlExecutionStart> Starts { get; } = [];

    public List<SqlExecutionEnd> Ends { get; } = [];

    public Task<SqlExecutionEntry> StartAsync(SqlExecutionStart start, CancellationToken cancellationToken)
    {
        Starts.Add(start);
        order?.Add("start");

        return Task.FromResult(new SqlExecutionEntry(++_nextId));
    }

    public Task CompleteAsync(SqlExecutionEntry entry, SqlExecutionEnd end)
    {
        Ends.Add(end);
        order?.Add("end");

        return Task.CompletedTask;
    }
}
