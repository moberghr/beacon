using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Common;

/// <summary>The real gate, recording the dialect of every request a caller sends it.</summary>
internal sealed class RecordingSqlGate(ISqlExecutionGate inner) : ISqlExecutionGate
{
    public List<string?> Dialects { get; } = [];

    public SqlGateReport Evaluate(SqlGateRequest request)
    {
        Dialects.Add(request.Dialect);

        return inner.Evaluate(request);
    }
}
