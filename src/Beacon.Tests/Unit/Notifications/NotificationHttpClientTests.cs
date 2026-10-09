using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// The outbound HTTP policy for notification calls: each type's named client (no redirects, no cookies, the
/// connect-time address check, HTTP/1.1 only, a timeout, a response size cap, no request logging), and the sender's
/// failure handling (the status, never the response body, not even in Debug logs). Redirects, private addresses and the
/// proxy are exercised end to end against loopback socket servers.
/// </summary>
[TestFixture]
public class NotificationHttpClientTests
{
    [TestCase(NotificationType.Webhook)]
    [TestCase(NotificationType.Slack)]
    [TestCase(NotificationType.Teams)]
    [TestCase(NotificationType.Jira)]
    public void NamedClient_UsesTheHardenedHandler(NotificationType type)
    {
        using var provider = Services();
        var handlers = HandlerChain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(NotificationHttpClient.NameFor(type)));

        var primary = handlers.OfType<SocketsHttpHandler>().Should().ContainSingle().Subject;
        primary.AllowAutoRedirect.Should().BeFalse();
        primary.UseCookies.Should().BeFalse();
        primary.UseProxy.Should().BeFalse("through a proxy the connect callback would vet the proxy, not the destination");
        primary.ConnectCallback.Should().NotBeNull();
        handlers.OfType<ResponseSizeLimitHandler>().Should().ContainSingle();
        handlers.OfType<NotificationRequestMarker>().Should().ContainSingle();
        handlers.Select(x => x.GetType().Name).Should().NotContain(x => x.Contains("Logging"), "notification URLs are secrets");
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(NotificationHttpClient.NameFor(type)).Timeout
            .Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void NamedClient_InProxyMode_UsesTheSystemProxyBehindTheDestinationCheck()
    {
        using var provider = Services(useSystemProxy: true);
        var handlers = HandlerChain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(NotificationHttpClient.NameFor(NotificationType.Webhook)));

        var primary = handlers.OfType<SocketsHttpHandler>().Should().ContainSingle().Subject;
        primary.UseProxy.Should().BeTrue();
        primary.AllowAutoRedirect.Should().BeFalse();
        primary.ConnectCallback.Should().NotBeNull("a destination the proxy configuration bypasses is still vetted");
        handlers.OfType<ProxiedDestinationCheckHandler>().Should().ContainSingle();
    }

    [Test]
    public async Task ProxyMode_PublicDestination_GoesThroughTheProxy()
    {
        using var proxy = CannedHttpServer.Start("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        var resolver = new FakeResolver { ["hooks.example.com"] = ["93.184.216.34"] };
        using var invoker = ProxyInvoker(resolver, proxy);

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://hooks.example.com/in"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        proxy.Requests.Should().Be(1);
        proxy.LastRequestLine.Should().StartWith("POST http://hooks.example.com/in", "the proxy is asked for the destination, not connected to as it");
    }

    [TestCase("10.0.0.5")]
    [TestCase("169.254.169.254")]
    public async Task ProxyMode_DestinationResolvingToABlockedAddress_NeverReachesTheProxy(string address)
    {
        using var proxy = CannedHttpServer.Start("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        var resolver = new FakeResolver { ["hooks.example.com"] = [address] };
        using var invoker = ProxyInvoker(resolver, proxy);

        var act = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://hooks.example.com/in"), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        NotificationFailureReasons.For(thrown.Which).Should().Be(NotificationFailureReasons.ConnectionBlocked);
        proxy.Requests.Should().Be(0);
    }

    [Test]
    public async Task ProxyMode_DestinationTheProxyBypasses_IsStillVettedWhenConnecting()
    {
        using var proxy = CannedHttpServer.Start("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        using var direct = CannedHttpServer.Start("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        var resolver = new FakeResolver { ["internal.example.com"] = ["127.0.0.1"] };
        var policy = new OutboundAddressPolicy(Options.Create(new NotificationChannelOptions { UseSystemProxy = true }), resolver);
        var primary = NotificationHttpClient.CreatePrimaryHandler(policy, NotificationType.Webhook);
        primary.Proxy = new WebProxy($"http://127.0.0.1:{proxy.Port}") { BypassList = ["internal\\.example\\.com"] };

        // The primary handler alone (no destination check in front): only the connect callback stands in the way.
        using var invoker = new HttpMessageInvoker(primary);
        var act = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"http://internal.example.com:{direct.Port}/in"), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        NotificationFailureReasons.For(thrown.Which).Should().Be(NotificationFailureReasons.ConnectionBlocked);
        proxy.Requests.Should().Be(0);
        direct.Requests.Should().Be(0);
    }

    [Test]
    public async Task DirectMode_DestinationCheckHandler_DoesNotResolveAnything()
    {
        var resolver = new FakeResolver();
        var policy = new OutboundAddressPolicy(Options.Create(new NotificationChannelOptions()), resolver);
        using var invoker = new HttpMessageInvoker(new ProxiedDestinationCheckHandler(policy, NotificationType.Webhook)
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
        });

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://hooks.example.com/in"), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        resolver.Lookups.Should().Be(0, "connecting directly, the connect callback does the check on the address it connects to");
    }

    [Test]
    public async Task Send_ToLoopback_IsRefusedByPolicyWithoutConnecting()
    {
        using var server = CannedHttpServer.Start("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        using var provider = Services();
        var sender = provider.GetRequiredService<NotificationHttpSender>();

        var act = () => sender.PostAsync(NotificationType.Webhook, server.Url, new StringContent("{}"), headers: null, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        NotificationFailureReasons.For(thrown.Which).Should().Be(NotificationFailureReasons.ConnectionBlocked);
        server.Requests.Should().Be(0);
    }

    [Test]
    public async Task Send_ToARedirect_DoesNotFollowIt()
    {
        using var server = CannedHttpServer.Start("HTTP/1.1 307 Temporary Redirect\r\nLocation: http://169.254.169.254/latest\r\nContent-Length: 0\r\n\r\n");
        using var provider = Services("127.0.0.1");
        var sender = provider.GetRequiredService<NotificationHttpSender>();

        var act = () => sender.PostAsync(NotificationType.Webhook, server.Url, new StringContent("{}"), headers: null, CancellationToken.None);

        await act.Should().ThrowAsync<NotificationDeliveryException>()
            .WithMessage("Notification delivery failed: the destination returned HTTP 3xx.");
        server.Requests.Should().Be(1, "the redirect is reported, not followed");
    }

    [Test]
    public async Task Send_ToAnErrorResponse_ReportsTheStatusAndNeverReadsOrLogsTheBody()
    {
        var body = "{\"error\":\"internal detail token=abc123\"}" + new string('x', 20_000);
        using var server = CannedHttpServer.Start($"HTTP/1.1 500 Internal Server Error\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        var logger = new CapturingLogger<NotificationHttpSender>();
        using var provider = Services(logger, "127.0.0.1");
        var sender = provider.GetRequiredService<NotificationHttpSender>();

        var act = () => sender.PostAsync(NotificationType.Webhook, server.Url, new StringContent("{}"), headers: null, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<NotificationDeliveryException>();
        thrown.Which.Message.Should().Be("Notification delivery failed: the destination returned HTTP 5xx.");
        ((int?)thrown.Which.StatusCode).Should().Be(500);
        thrown.Which.InnerException.Should().BeNull();
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Debug);
        entry.Message.Should().Contain("500").And.Contain("application/json").And.NotContain("abc123").And.NotContain(server.Url);
    }

    [Test]
    public async Task Send_ToAPrivateAddressAllowedForAnotherType_IsRefused()
    {
        using var server = CannedHttpServer.Start("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        using var provider = Services(NotificationType.Jira, "127.0.0.1");
        var sender = provider.GetRequiredService<NotificationHttpSender>();

        var webhook = () => sender.PostAsync(NotificationType.Webhook, server.Url, new StringContent("{}"), headers: null, CancellationToken.None);

        var thrown = await webhook.Should().ThrowAsync<HttpRequestException>();
        NotificationFailureReasons.For(thrown.Which).Should().Be(NotificationFailureReasons.ConnectionBlocked);
        server.Requests.Should().Be(0);
    }

    [Test]
    public async Task Requests_ArePinnedToHttp11()
    {
        HttpRequestMessage? captured = null;
        var invoker = new HttpMessageInvoker(new NotificationRequestMarker
        {
            InnerHandler = new StubHandler(x =>
            {
                captured = x;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
        });

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://hooks.example.com/in") { Version = HttpVersion.Version20 }, CancellationToken.None);

        captured!.Version.Should().Be(HttpVersion.Version11);
        captured.VersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionExact);
    }

    [Test]
    public async Task Send_AppliesTheGivenHeadersToTheRequestOnly()
    {
        HttpRequestMessage? captured = null;
        var sender = Sender(new StubHandler(x =>
        {
            captured = x;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await sender.PostAsync(
            NotificationType.Webhook,
            "https://hooks.example.com/in",
            new StringContent("{}"),
            new Dictionary<string, string> { ["Authorization"] = "Bearer t", ["Api-Key"] = "k" },
            CancellationToken.None);

        captured!.Method.Should().Be(HttpMethod.Post);
        captured.Headers.GetValues("Authorization").Should().Equal("Bearer t");
        captured.Headers.GetValues("Api-Key").Should().Equal("k");
    }

    [Test]
    public async Task Requests_OnTheNotificationClient_AreMarkedForTracingFilters()
    {
        HttpRequestMessage? captured = null;
        var invoker = new HttpMessageInvoker(new NotificationRequestMarker
        {
            InnerHandler = new StubHandler(x =>
            {
                captured = x;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
        });

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://hooks.slack.com/services/T0/B0/x"), CancellationToken.None);

        NotificationRequests.IsNotificationRequest(captured!).Should().BeTrue();
        NotificationRequests.IsNotificationRequest(new HttpRequestMessage(HttpMethod.Get, "https://example.com/")).Should().BeFalse();
    }

    [Test]
    public async Task ResponseSizeLimit_DeclaredLengthOverTheCap_FailsImmediately()
    {
        var invoker = new HttpMessageInvoker(new ResponseSizeLimitHandler(1024, TimeSpan.FromSeconds(30))
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) }),
        });

        var act = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://jira.example.com/"), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*size limit*");
    }

    [Test]
    public async Task ResponseSizeLimit_UndeclaredLengthOverTheCap_FailsWhileReading()
    {
        var invoker = new HttpMessageInvoker(new ResponseSizeLimitHandler(1024, TimeSpan.FromSeconds(30))
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnseekableStream(new byte[4096])),
            }),
        });

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://jira.example.com/"), CancellationToken.None);
        var act = () => response.Content.ReadAsStringAsync();

        response.Content.Headers.ContentLength.Should().BeNull();
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*size limit*");
    }

    [Test]
    public async Task ResponseSizeLimit_BodyWithinTheCap_IsReadIntact()
    {
        var invoker = new HttpMessageInvoker(new ResponseSizeLimitHandler(1024, TimeSpan.FromSeconds(30))
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"key\":\"SEC-1\"}", Encoding.UTF8, "application/json"),
            }),
        });

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://jira.example.com/"), CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).Should().Be("{\"key\":\"SEC-1\"}");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Test]
    public async Task ResponseSizeLimit_BodyThatNeverFinishes_FailsAtTheReadDeadline()
    {
        var invoker = new HttpMessageInvoker(new ResponseSizeLimitHandler(1024, TimeSpan.FromMilliseconds(200))
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }),
        });

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://jira.example.com/"), CancellationToken.None);
        var act = () => response.Content.ReadAsStringAsync();

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*time limit*");
    }

    [Test]
    public async Task ProxyMode_Https_TunnelsThroughTheProxyWithConnect()
    {
        using var proxy = CannedHttpServer.Start("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
        var resolver = new FakeResolver { ["hooks.example.com"] = ["93.184.216.34"] };
        using var invoker = ProxyInvoker(resolver, proxy);

        var act = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://hooks.example.com/in"), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>("the canned proxy refuses the tunnel");
        proxy.LastRequestLine.Should().Be("CONNECT hooks.example.com:443 HTTP/1.1", "only the host and port reach the proxy in clear text");
    }

    private static HttpMessageInvoker ProxyInvoker(FakeResolver resolver, CannedHttpServer proxy)
    {
        var options = new NotificationChannelOptions { UseSystemProxy = true };
        var policy = new OutboundAddressPolicy(Options.Create(options), resolver);
        var primary = NotificationHttpClient.CreatePrimaryHandler(policy, NotificationType.Webhook);

        // Stands in for the system proxy (HttpClient.DefaultProxy), which a test must not change process-wide.
        primary.Proxy = new WebProxy($"http://127.0.0.1:{proxy.Port}");

        return new HttpMessageInvoker(new ProxiedDestinationCheckHandler(policy, NotificationType.Webhook) { InnerHandler = primary });
    }

    private static ServiceProvider Services(params string[] webhookPrivateNetworks)
    {
        return Services(NotificationTestKit.PrivateNetworks(NotificationType.Webhook, webhookPrivateNetworks), logger: null);
    }

    private static ServiceProvider Services(NotificationType type, params string[] privateNetworks)
    {
        return Services(NotificationTestKit.PrivateNetworks(type, privateNetworks), logger: null);
    }

    private static ServiceProvider Services(CapturingLogger<NotificationHttpSender> logger, params string[] webhookPrivateNetworks)
    {
        return Services(NotificationTestKit.PrivateNetworks(NotificationType.Webhook, webhookPrivateNetworks), logger);
    }

    private static ServiceProvider Services(bool useSystemProxy)
    {
        var options = new NotificationChannelOptions { UseSystemProxy = useSystemProxy };
        options.AllowedHosts.Webhook.Add("hooks.example.com");
        return Services(options, logger: null);
    }

    private static ServiceProvider Services(NotificationChannelOptions options, CapturingLogger<NotificationHttpSender>? logger)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IHostAddressResolver>(new FakeResolver());
        services.AddSingleton<OutboundAddressPolicy>();
        if (logger != null)
        {
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<NotificationHttpSender>>(logger);
        }

        services.AddSingleton<NotificationHttpSender>();
        services.AddNotificationHttpClient();

        return services.BuildServiceProvider();
    }

    private static NotificationHttpSender Sender(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(x => x.CreateClient(NotificationHttpClient.NameFor(NotificationType.Webhook)))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new NotificationHttpSender(factory.Object, NullLogger<NotificationHttpSender>.Instance);
    }

    private static List<HttpMessageHandler> HandlerChain(HttpMessageHandler handler)
    {
        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? current = handler; current != null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(current);
        }

        return chain;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A body that sends nothing and never ends, like a stalled or deliberately slow server.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body whose length the client cannot know up front, like a chunked response.</summary>
    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    /// <summary>Accepts loopback connections, counts requests and answers each with the same raw response.</summary>
    private sealed class CannedHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _response;
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        private CannedHttpServer(string response)
        {
            _response = response;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string Url => $"http://127.0.0.1:{Port}/hook";

        public int Requests => Volatile.Read(ref _requests);

        public string? LastRequestLine { get; private set; }

        public static CannedHttpServer Start(string response)
        {
            return new CannedHttpServer(response);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = AnswerAsync(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var received = new StringBuilder();
                while (!received.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                LastRequestLine = received.ToString().Split("\r\n")[0];
                Interlocked.Increment(ref _requests);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(_response), _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
        }
    }
}
