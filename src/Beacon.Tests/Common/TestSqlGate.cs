using Microsoft.Extensions.Logging.Abstractions;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Common;

/// <summary>
/// Builds a real <see cref="SqlExecutionGate"/> over the real validators. Fixtures that already mock
/// <see cref="IQueryGuardrailService"/> pass their mock so existing guardrail setups keep applying.
/// </summary>
internal static class TestSqlGate
{
    public static SqlExecutionGate Create(IQueryGuardrailService? guardrail = null)
    {
        return new SqlExecutionGate(
            guardrail ?? new QueryGuardrailService(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new SqlSchemaValidator(),
            new SqlSemanticLinter(),
            NullLogger<SqlExecutionGate>.Instance);
    }

    public static McpSettingsData DefaultSettings()
    {
        return new McpSettingsData();
    }
}
