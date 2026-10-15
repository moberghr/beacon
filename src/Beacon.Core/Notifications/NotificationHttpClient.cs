using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Notifications;

/// <summary>
/// The one outbound HTTP policy for notification adapters (Slack, Teams, webhook and Jira): one named client per type,
/// so each type's private-network allowances apply only to its own calls and connections are never pooled across
/// types. Every client's handler never follows redirects, connects directly and only through
/// <see cref="OutboundAddressPolicy"/> (or, with <c>Beacon:Notifications:UseSystemProxy</c>, through the system proxy
/// after <see cref="ProxiedDestinationCheckHandler"/> has checked the destination), keeps no cookies, speaks HTTP/1.1
/// only, gives up after <see cref="Timeout"/> (and allows the same again for reading a body, which the client's own
/// timeout does not cover once headers have arrived), and caps every response body at <see cref="MaxResponseBytes"/>.
/// The clients' request logging is removed because notification URLs are themselves secrets; every request is marked so
/// a host's HTTP tracing can leave it out (<see cref="NotificationRequests.IsNotificationRequest"/>).
/// </summary>
internal static class NotificationHttpClient
{
    public const long MaxResponseBytes = 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static string NameFor(NotificationType type)
    {
        return $"Beacon.Notifications.{type}";
    }

    public static IServiceCollection AddNotificationHttpClient(this IServiceCollection services)
    {
        foreach (var type in OutboundAddressPolicy.HttpTypes)
        {
            services
                .AddHttpClient(NameFor(type))
                .ConfigureHttpClient(x => x.Timeout = Timeout)
                .ConfigurePrimaryHttpMessageHandler(x => CreatePrimaryHandler(x.GetRequiredService<OutboundAddressPolicy>(), type))
                .AddHttpMessageHandler(() => new NotificationRequestMarker())
                .AddHttpMessageHandler(x => new ProxiedDestinationCheckHandler(x.GetRequiredService<OutboundAddressPolicy>(), type))
                .AddHttpMessageHandler(() => new ResponseSizeLimitHandler(MaxResponseBytes, Timeout))
                .RemoveAllLoggers();
        }

        return services;
    }

    public static SocketsHttpHandler CreatePrimaryHandler(OutboundAddressPolicy addressPolicy, NotificationType type)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            // Off by default: through a proxy the connect callback would see only the proxy. With UseSystemProxy the
            // destination is checked before sending instead (ProxiedDestinationCheckHandler).
            UseProxy = addressPolicy.UsesSystemProxy,
            ConnectTimeout = ConnectTimeout,
            ConnectCallback = (context, cancellationToken) => addressPolicy.ConnectAsync(type, context, cancellationToken),
            // Re-resolve (and re-check) hosts regularly instead of pinning a connection forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
    }

    /// <summary>The exception types of a failure, outermost first ("HttpRequestException &gt; SocketException"), for logs.</summary>
    public static string TypeChain(Exception exception)
    {
        var types = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            types.Add(current.GetType().Name);
        }

        return string.Join(" > ", types);
    }
}

/// <summary>
/// Identifies requests made by notification adapters. Their URLs are secrets (a Slack webhook's token is in its path),
/// so a host that traces outgoing HTTP should leave them out, for example with OpenTelemetry's
/// <c>AddHttpClientInstrumentation(x =&gt; x.FilterHttpRequestMessage = y =&gt; !NotificationRequests.IsNotificationRequest(y))</c>.
/// </summary>
public static class NotificationRequests
{
    internal static readonly HttpRequestOptionsKey<bool> Marker = new("Beacon.NotificationRequest");

    public static bool IsNotificationRequest(HttpRequestMessage request)
    {
        return request.Options.TryGetValue(Marker, out var marked) && marked;
    }
}

/// <summary>
/// In proxy mode, refuses a request whose destination resolves to any address the type's policy blocks, before it
/// reaches the proxy (which only the connect callback would otherwise see). Does nothing when connecting directly.
/// </summary>
internal sealed class ProxiedDestinationCheckHandler(OutboundAddressPolicy addressPolicy, NotificationType type) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (addressPolicy.UsesSystemProxy && request.RequestUri != null)
        {
            try
            {
                await addressPolicy.EnsureDestinationAllowedAsync(type, request.RequestUri, cancellationToken);
            }
            catch (OutboundConnectionBlockedException ex)
            {
                throw new HttpRequestException("The destination address is not allowed for outbound notification calls.", ex);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Runs first on every notification request: marks it for tracing filters (before the handler that starts the tracing
/// activity) and pins it to exactly HTTP/1.1, whoever built it (the sender or Jira's Refit client).
/// </summary>
internal sealed class NotificationRequestMarker : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.Set(NotificationRequests.Marker, true);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Posts notification payloads through the type's <see cref="NotificationHttpClient"/>. A response that is not a
/// success becomes a <see cref="NotificationDeliveryException"/> carrying the status; the body is never read or logged
/// (§1.11), only its status, content type and declared length, at Debug level.
/// </summary>
internal sealed class NotificationHttpSender(IHttpClientFactory httpClientFactory, ILogger<NotificationHttpSender> logger)
{
    public async Task PostAsync(
        NotificationType type,
        string destination,
        HttpContent content,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, destination);
        request.Content = content;

        if (headers != null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        var client = httpClientFactory.CreateClient(NotificationHttpClient.NameFor(type));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        logger.LogDebug(
            "{NotificationType} destination returned HTTP {StatusCode} ({ContentType}, {ContentLength} bytes)",
            type,
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            response.Content.Headers.ContentLength);

        throw NotificationDeliveryException.ForStatus(response.StatusCode);
    }
}

/// <summary>
/// Caps a response body: a declared <c>Content-Length</c> over the limit fails at once, an undeclared body fails as soon
/// as reading passes the limit, before it is all buffered, and reading the body fails once
/// <paramref name="readTimeout"/> has passed since the headers arrived.
/// </summary>
internal sealed class ResponseSizeLimitHandler(long maxBytes, TimeSpan readTimeout) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            response.Dispose();
            throw new HttpRequestException("The notification destination's response exceeded the size limit.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var limited = new StreamContent(new LengthLimitedStream(stream, maxBytes, readTimeout));
        foreach (var header in response.Content.Headers)
        {
            limited.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = limited;
        return response;
    }
}

/// <summary>
/// A read-only stream that throws once more than its limit has been read from the inner stream, or once its read
/// deadline has passed.
/// </summary>
internal sealed class LengthLimitedStream(Stream inner, long maxBytes, TimeSpan readTimeout) : Stream
{
    private readonly CancellationTokenSource _deadline = new(readTimeout);
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        try
        {
            return Count(await inner.ReadAsync(buffer, linked.Token));
        }
        catch (OperationCanceledException) when (_deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The notification destination's response was not read within the time limit.");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            _deadline.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Count(int read)
    {
        _read += read;
        if (_read > maxBytes)
        {
            throw new HttpRequestException("The notification destination's response exceeded the size limit.");
        }

        return read;
    }
}
