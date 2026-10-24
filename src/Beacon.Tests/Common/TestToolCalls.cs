using System.Runtime.CompilerServices;
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
    private static readonly ConditionalWeakTable<object, Func<McpToolCallRecorder, object>?> TrackedTools = [];
    private static readonly ConditionalWeakTable<object, Func<McpToolCallRecorder>> TrackedRecorders = [];

    /// <summary>A recorder writing through <paramref name="auditService"/>; an audit-only tool never reaches its signal service.</summary>
    public static McpToolCallRecorder Recorder(McpAuditService auditService) =>
        Recorder(
            auditService,
            new McpSignalService(
                Mock.Of<IDbContextFactory<BeaconContext>>(),
                SettingsProviderMock.Create().Object,
                NullLogger<McpSignalService>.Instance));

    /// <summary>A recorder writing the audit row through <paramref name="auditService"/> and the signal through <paramref name="signalService"/>.</summary>
    public static McpToolCallRecorder Recorder(McpAuditService auditService, McpSignalService signalService) =>
        new(auditService, signalService, new McpAuditOutcome(), NullLogger<McpToolCallRecorder>.Instance);

    /// <summary>
    /// Builds a tool on a recorder from <paramref name="newRecorder"/> and remembers how, for
    /// <see cref="InvokeAsync{TTool}"/>: each call runs on a tool built on a fresh recorder (one call per recorder, as one
    /// call per request scope), writing through the same services.
    /// </summary>
    public static TTool Track<TTool>(Func<McpToolCallRecorder, TTool> build, Func<McpToolCallRecorder> newRecorder)
        where TTool : class
    {
        var tool = build(newRecorder());
        TrackedTools.AddOrUpdate(tool, x => build(x));
        TrackedRecorders.AddOrUpdate(tool, newRecorder);

        return tool;
    }

    /// <summary>Runs <paramref name="call"/> on a tool built through <see cref="Track{TTool}"/>, through the audit filter.</summary>
    public static Task<CallToolResult> InvokeAsync<TTool>(TTool tool, Func<TTool, Task<CallToolResult>> call)
        where TTool : class
    {
        if (!TrackedTools.TryGetValue(tool, out var build) || !TrackedRecorders.TryGetValue(tool, out var newRecorder))
        {
            throw new InvalidOperationException($"The {typeof(TTool).Name} was not built through TestToolCalls.Track.");
        }

        var recorder = newRecorder();
        var fresh = (TTool)build!(recorder);

        return InvokeAsync(recorder, () => call(fresh));
    }

    /// <summary>Runs <paramref name="tool"/> through the audit filter; the recorded call is written before this returns.</summary>
    public static async Task<CallToolResult> InvokeAsync(McpToolCallRecorder recorder, Func<Task<CallToolResult>> tool)
    {
        await using var services = new ServiceCollection()
            .AddSingleton(recorder)
            .BuildServiceProvider();

        return await McpAuditCallToolFilter.InvokeAsync(services, null, async _ => await tool(), CancellationToken.None);
    }
}
