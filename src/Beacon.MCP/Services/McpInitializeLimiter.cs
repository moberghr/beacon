using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using Beacon.Core.Configuration;
using Beacon.Core.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beacon.MCP.Services;

/// <summary>
/// Caps how many MCP <c>initialize</c> requests one caller may send in a window (<see cref="RequestLimitsOptions"/>), so a
/// caller cannot open sessions without bound. The SDK offers an idle-session cap but no per-caller one. In memory and per
/// instance; counts every attempt, refused ones included.
/// </summary>
internal sealed class McpInitializeLimiter(
    IOptions<RequestLimitsOptions> options,
    ILogger<McpInitializeLimiter> logger,
    TimeProvider? timeProvider = null)
{
    private const int MaxTrackedCallers = 10_000;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Queue<long>> _attempts = new(StringComparer.Ordinal);

    /// <summary>Records one initialize attempt; false when the caller is over the limit.</summary>
    public bool TryAcquire(string callerKey)
    {
        var limits = options.Value;
        if (limits.McpMaxInitializesPerCaller <= 0 || limits.McpInitializeWindowSeconds <= 0)
        {
            return true;
        }

        var now = _time.GetTimestamp();
        var window = (long)(limits.McpInitializeWindowSeconds * (double)_time.TimestampFrequency);

        if (_attempts.Count >= MaxTrackedCallers)
        {
            Sweep(now, window);
        }

        var queue = _attempts.GetOrAdd(callerKey, _ => new Queue<long>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limits.McpMaxInitializesPerCaller)
            {
                return false;
            }

            queue.Enqueue(now);

            return true;
        }
    }

    /// <summary>
    /// The endpoint wrapper: a POST without a session header whose body is an <c>initialize</c> request counts against the
    /// caller; over the limit it is answered 429 before a session exists.
    /// </summary>
    public async Task<bool> AdmitAsync(HttpContext context)
    {
        var limits = options.Value;
        if (limits.McpMaxInitializesPerCaller <= 0
            || !HttpMethods.IsPost(context.Request.Method)
            || context.Request.Headers.ContainsKey("Mcp-Session-Id"))
        {
            return true;
        }

        if (!await IsInitializeAsync(context.Request, context.RequestAborted))
        {
            return true;
        }

        var key = CallerKey(context);
        if (TryAcquire(key))
        {
            return true;
        }

        // Identifiers only (§1.11): the key id or caller hash, never a name or an address.
        logger.LogWarning("MCP initialize refused: too many sessions opened by one caller. Caller={Caller}", key.StartsWith("ip:", StringComparison.Ordinal) ? "address" : key);
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = Math.Max(1, limits.McpInitializeWindowSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        return false;
    }

    internal static string CallerKey(HttpContext context)
    {
        var user = context.User;
        var apiKeyId = user.FindFirst(McpCallerClaimTypes.ApiKeyId)?.Value;
        if (!string.IsNullOrEmpty(apiKeyId))
        {
            return $"key:{apiKeyId}";
        }

        var callerHash = user.FindFirst(McpCallerClaimTypes.CallerHash)?.Value;
        if (!string.IsNullOrEmpty(callerHash))
        {
            return $"caller:{callerHash}";
        }

        var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(subject))
        {
            return $"user:{subject}";
        }

        return $"ip:{context.Connection.RemoteIpAddress}";
    }

    private static async Task<bool> IsInitializeAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("method", out var method)
                && method.ValueKind == JsonValueKind.String
                && method.ValueEquals("initialize");
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private void Sweep(long now, long window)
    {
        foreach (var entry in _attempts)
        {
            lock (entry.Value)
            {
                if (entry.Value.Count == 0 || now - entry.Value.Last() >= window)
                {
                    _attempts.TryRemove(entry.Key, out _);
                }
            }
        }
    }
}
