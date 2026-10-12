using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Common;

/// <summary>
/// Builds a real <see cref="SqlExecutionGate"/> over the real validators. Fixtures that already mock
/// <see cref="IQueryGuardrailService"/> pass their mock so existing guardrail setups keep applying. Without
/// <c>deployment</c> the gate runs under the default deployment options, whose read-only lock is on; a test of read-only
/// enforcement switched off passes <see cref="Unlocked"/>.
/// </summary>
internal static class TestSqlGate
{
    /// <summary>A deployment that lets read-only enforcement and PII detection be switched off.</summary>
    public static McpDeploymentOptions Unlocked => new() { ForceReadOnly = false, ForcePiiDetection = false };

    public static SqlExecutionGate Create(
        IQueryGuardrailService? guardrail = null,
        Beacon.Core.HostData.IHostDataSourceGuard? hostGuard = null,
        McpDeploymentOptions? deployment = null)
    {
        return new SqlExecutionGate(
            guardrail ?? new QueryGuardrailService(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new SqlSchemaValidator(),
            new SqlSemanticLinter(),
            NullLogger<SqlExecutionGate>.Instance,
            hostGuard,
            Options.Create(deployment ?? new McpDeploymentOptions()));
    }

    public static McpSettingsData DefaultSettings()
    {
        return new McpSettingsData();
    }
}
