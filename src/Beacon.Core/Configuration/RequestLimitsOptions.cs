using Microsoft.Extensions.Options;

namespace Beacon.Core.Configuration;

/// <summary>
/// Bounds on what Beacon's own routes (<c>/beacon/api</c>, the hub under it and <c>/beacon/mcp</c>) accept, bound from
/// <c>Beacon:RequestLimits</c>. They apply to Beacon's routes only, and a host's own limit wins where it is stricter (a
/// lower Kestrel <c>MaxRequestBodySize</c> is never raised). A value of 0 turns that one bound off. The Kestrel
/// keep-alive and header timeouts and the 5-minute <c>HttpClient</c> timeout the AI calls depend on are not touched here.
/// </summary>
public sealed class RequestLimitsOptions
{
    public const string SectionName = "Beacon:RequestLimits";

    /// <summary>Largest request body on <c>/beacon/api</c>, in bytes. Default 2 MiB.</summary>
    public long ApiMaxBodyBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Largest request body on <c>/beacon/mcp</c>, in bytes. Default 4 MiB.</summary>
    public long McpMaxBodyBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// How long one <c>/beacon/api</c> request may run before its cancellation token is cancelled (the hub and the routes that
    /// run long work inline, such as a migration run, a metadata refresh or an LLM flow of several calls, are exempt and keep
    /// their own timeouts). Seconds.
    /// </summary>
    public int ApiTimeoutSeconds { get; set; } = 300;

    /// <summary>Largest message the realtime hub accepts from a client. The hub has no client-callable methods. Default 8 KiB.</summary>
    public long HubMaxReceiveMessageBytes { get; set; } = 8 * 1024;

    /// <summary>Hub invocations running at once per connection. Default 1.</summary>
    public int HubMaxParallelInvocationsPerClient { get; set; } = 1;

    /// <summary>Seconds without a client message before the hub drops the connection. Default 30 (the SignalR default, made explicit).</summary>
    public int HubClientTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Idle MCP sessions kept in memory (<c>HttpServerTransportOptions.MaxIdleSessionCount</c>); the SDK's own default is
    /// 10,000. The idle timeout itself is 30 minutes. Default 1,000.
    /// </summary>
    public int McpMaxIdleSessions { get; set; } = 1_000;

    /// <summary>
    /// MCP <c>initialize</c> requests one caller (API key, mapped caller, user or address) may send per
    /// <see cref="McpInitializeWindowSeconds"/>; more are answered 429. Default 60.
    /// </summary>
    public int McpMaxInitializesPerCaller { get; set; } = 60;

    /// <summary>The window of <see cref="McpMaxInitializesPerCaller"/>, in seconds. Default 60.</summary>
    public int McpInitializeWindowSeconds { get; set; } = 60;
}

internal sealed class RequestLimitsOptionsValidator : IValidateOptions<RequestLimitsOptions>
{
    public ValidateOptionsResult Validate(string? name, RequestLimitsOptions options)
    {
        var failures = new List<string>();
        Require(failures, nameof(RequestLimitsOptions.ApiMaxBodyBytes), options.ApiMaxBodyBytes);
        Require(failures, nameof(RequestLimitsOptions.McpMaxBodyBytes), options.McpMaxBodyBytes);
        Require(failures, nameof(RequestLimitsOptions.ApiTimeoutSeconds), options.ApiTimeoutSeconds);
        Require(failures, nameof(RequestLimitsOptions.HubMaxReceiveMessageBytes), options.HubMaxReceiveMessageBytes);
        Require(failures, nameof(RequestLimitsOptions.HubMaxParallelInvocationsPerClient), options.HubMaxParallelInvocationsPerClient);
        Require(failures, nameof(RequestLimitsOptions.HubClientTimeoutSeconds), options.HubClientTimeoutSeconds);
        Require(failures, nameof(RequestLimitsOptions.McpMaxIdleSessions), options.McpMaxIdleSessions);
        Require(failures, nameof(RequestLimitsOptions.McpMaxInitializesPerCaller), options.McpMaxInitializesPerCaller);
        Require(failures, nameof(RequestLimitsOptions.McpInitializeWindowSeconds), options.McpInitializeWindowSeconds);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, string property, long value)
    {
        if (value < 0)
        {
            failures.Add($"{RequestLimitsOptions.SectionName}:{property} must not be negative (0 turns the bound off).");
        }
    }
}
