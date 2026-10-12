using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Common;

/// <summary>The real gate, recording every request a caller sends it (and its dialect) with the report it got back.</summary>
internal sealed class RecordingSqlGate(ISqlExecutionGate inner) : ISqlExecutionGate
{
    public List<string?> Dialects { get; } = [];

    public List<(SqlGateRequest Request, SqlGateReport Report)> Evaluations { get; } = [];

    public SqlGateReport Evaluate(SqlGateRequest request)
    {
        Dialects.Add(request.Dialect);
        var report = inner.Evaluate(request);
        Evaluations.Add((request, report));

        return report;
    }
}
