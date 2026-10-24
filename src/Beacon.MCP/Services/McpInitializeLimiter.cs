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
/// instance; only attempts that were admitted count, so the window slides while a caller keeps being refused.
/// </summary>
internal sealed class McpInitializeLimiter(
    IOptions<RequestLimitsOptions> options,
    ILogger<McpInitializeLimiter> logger,
    TimeProvider? timeProvider = null)
{
    private const int MaxTrackedCallers = 10_000;
    private const string OverflowKey = "overflow";
    // The method of a JSON-RPC request is read from this much of the body; the rest is never buffered or parsed here.
    private const int PeekBytes = 8 * 1024;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Bucket> _attempts = new(StringComparer.Ordinal);
    private long _nextSweep;

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

        if (_attempts.Count >= MaxTrackedCallers && now >= Interlocked.Read(ref _nextSweep))
        {
            Sweep(now, window);
            Interlocked.Exchange(ref _nextSweep, now + window);
        }

        // A caller that would grow the map past twice the sweep threshold shares one bucket, so memory stays bounded.
        if (_attempts.Count >= MaxTrackedCallers * 2 && !_attempts.ContainsKey(callerKey))
        {
            callerKey = OverflowKey;
        }

        while (true)
        {
            var bucket = _attempts.GetOrAdd(callerKey, _ => new Bucket());
            lock (bucket)
            {
                if (bucket.Removed)
                {
                    continue;
                }

                var queue = bucket.Attempts;
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
        var buffer = new byte[PeekBytes];
        var read = 0;
        try
        {
            while (read < buffer.Length)
            {
                var count = await request.Body.ReadAsync(buffer.AsMemory(read), cancellationToken);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }
        }
        finally
        {
            request.Body.Position = 0;
        }

        return MayBeInitialize(buffer.AsSpan(0, read), complete: read < buffer.Length);
    }

    // Reads the top-level "method" from the prefix. A method that cannot be read because the prefix ends first counts as an
    // initialize: a request that hides its method behind a large body is not a normal client's.
    internal static bool MayBeInitialize(ReadOnlySpan<byte> prefix, bool complete)
    {
        var reader = new Utf8JsonReader(prefix, isFinalBlock: complete, default);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return false;
                }

                var isMethod = reader.ValueTextEquals("method"u8);
                if (!reader.Read())
                {
                    break;
                }

                if (isMethod)
                {
                    return reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("initialize"u8);
                }

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !SkipValue(ref reader))
                {
                    break;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return !complete;
    }

    private static bool SkipValue(ref Utf8JsonReader reader)
    {
        var depth = 1;
        while (depth > 0)
        {
            if (!reader.Read())
            {
                return false;
            }

            depth += reader.TokenType switch
            {
                JsonTokenType.StartObject or JsonTokenType.StartArray => 1,
                JsonTokenType.EndObject or JsonTokenType.EndArray => -1,
                _ => 0
            };
        }

        return true;
    }

    private void Sweep(long now, long window)
    {
        foreach (var entry in _attempts)
        {
            lock (entry.Value)
            {
                if (entry.Value.Attempts.Count == 0 || now - entry.Value.Attempts.Last() >= window)
                {
                    entry.Value.Removed = true;
                    _attempts.TryRemove(entry.Key, out _);
                }
            }
        }
    }

    private sealed class Bucket
    {
        public Queue<long> Attempts { get; } = new();

        public bool Removed { get; set; }
    }
}
