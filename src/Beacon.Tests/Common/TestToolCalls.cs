using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Moq;
using Beacon.Core.Data;
using Beacon.MCP.Services;

namespace Beacon.Tests.Common;

/// <summary>
/// Calls a built-in MCP tool outside the transport the way the transport calls it: through
/// <see cref="McpAuditCallToolFilter"/>, which writes the call the tool kept on its <see cref="McpToolCallRecorder"/> after
/// the tool returned or threw (§1.7).
/// </summary>
internal static class TestToolCalls
{
    /// <summary>A recorder writing through <paramref name="auditService"/>; an audit-only tool never reaches its signal service.</summary>
    public static McpToolCallRecorder Recorder(McpAuditService auditService) =>
        new(
            auditService,
            new McpSignalService(
                Mock.Of<IDbContextFactory<BeaconContext>>(),
                SettingsProviderMock.Create().Object,
                NullLogger<McpSignalService>.Instance),
            new McpAuditOutcome(),
            NullLogger<McpToolCallRecorder>.Instance);

    /// <summary>Runs <paramref name="tool"/> through the audit filter; the recorded call is written before this returns.</summary>
    public static async Task<CallToolResult> InvokeAsync(McpToolCallRecorder recorder, Func<Task<CallToolResult>> tool)
    {
        await using var services = new ServiceCollection()
            .AddSingleton(recorder)
            .BuildServiceProvider();

        return await McpAuditCallToolFilter.InvokeAsync(services, null, async _ => await tool(), CancellationToken.None);
    }
}
