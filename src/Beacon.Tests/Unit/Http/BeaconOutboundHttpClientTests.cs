using System.Net;
using System.Net.Sockets;
using System.Text;
using Beacon.Core.Http;
using Beacon.Core.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Http;

/// <summary>
/// The named clients for the JWKS fetch, Databricks and GitHub: no redirects (a credential header must not follow one),
/// their own timeouts, and a response size cap on the JWKS and Databricks clients. Redirect and size behaviour runs
/// against a loopback socket server through the real handler chain.
/// </summary>
[TestFixture]
public class BeaconOutboundHttpClientTests
{
    [TestCase(BeaconHttpClients.Jwks)]
    [TestCase(BeaconHttpClients.Databricks)]
    [TestCase(BeaconHttpClients.GitHub)]
    public void NamedClient_NeverFollowsRedirectsOrKeepsCookies(string name)
    {
        using var provider = Services();

        var primary = HandlerChain(provider, name).OfType<SocketsHttpHandler>().Should().ContainSingle().Subject;

        primary.AllowAutoRedirect.Should().BeFalse();
        primary.UseCookies.Should().BeFalse();
    }

    [Test]
    public void JwksClient_TimesOutAfterTenSeconds_AndCapsTheBodyAt256Kb()
    {
        using var provider = Services();

        provider.GetRequiredService<IHttpClientFactory>().CreateClient(BeaconHttpClients.Jwks).Timeout
            .Should().Be(TimeSpan.FromSeconds(10));
        HandlerChain(provider, BeaconHttpClients.Jwks).OfType<ResponseSizeLimitHandler>().Should().ContainSingle()
            .Which.MaxBytes.Should().Be(256 * 1024);
    }

    [Test]
    public void DatabricksClient_CapsTheBody()
    {
        using var provider = Services();

        HandlerChain(provider, BeaconHttpClients.Databricks).OfType<ResponseSizeLimitHandler>().Should().ContainSingle()
            .Which.MaxBytes.Should().Be(BeaconHttpClients.DatabricksMaxResponseBytes);
    }

    [Test]
    public void GitHubClient_TimesOutAfterSixtySeconds()
    {
        using var provider = Services();

        provider.GetRequiredService<IHttpClientFactory>().CreateClient(BeaconHttpClients.GitHub).Timeout
            .Should().Be(TimeSpan.FromSeconds(60));
    }

    [Test]
    public void RegisteringTwice_AddsEachHandlerOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconOutboundHttpClients();
        services.AddBeaconOutboundHttpClients();
        using var provider = services.BuildServiceProvider();

        HandlerChain(provider, BeaconHttpClients.Jwks).OfType<ResponseSizeLimitHandler>().Should().ContainSingle();
    }

    [Test]
    public async Task DatabricksClient_ReturnsARedirectAsIs_WithoutRequestingTheTarget()
    {
        using var server = new LoopbackServer(_ => Redirect);
        using var provider = Services();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(BeaconHttpClients.Databricks);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");

        using var response = await client.GetAsync(server.Url("/start"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        server.Requests.Should().Equal("/start");
    }

    [TestCase(BeaconHttpClients.Jwks)]
    [TestCase(BeaconHttpClients.GitHub)]
    public async Task NamedClient_RefusesARedirectWithAClearMessage_WithoutRequestingTheTarget(string name)
    {
        using var server = new LoopbackServer(_ => Redirect);
        using var provider = Services();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");

        var act = () => client.GetAsync(server.Url("/start"));

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("redirect to another location").And.Contain("configure the final URL");
        server.Requests.Should().Equal("/start");
    }

    private const string Redirect = "HTTP/1.1 302 Found\r\nLocation: /elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    [Test]
    public async Task JwksClient_RefusesABodyOverTheCap()
    {
        using var server = new LoopbackServer(_ => Respond(new string('k', 256 * 1024 + 1)));
        using var provider = Services();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(BeaconHttpClients.Jwks);

        var act = () => client.GetStringAsync(server.Url("/jwks"));

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*size limit*");
    }

    [Test]
    public async Task JwksClient_ReadsABodyWithinTheCap()
    {
        using var server = new LoopbackServer(_ => Respond("{\"keys\":[]}"));
        using var provider = Services();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(BeaconHttpClients.Jwks);

        var body = await client.GetStringAsync(server.Url("/jwks"));

        body.Should().Be("{\"keys\":[]}");
    }

    private static string Respond(string body)
    {
        return $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconOutboundHttpClients();
        return services.BuildServiceProvider();
    }

    private static List<HttpMessageHandler> HandlerChain(ServiceProvider provider, string name)
    {
        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? current = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
             current != null;
             current = (current as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(current);
        }

        return chain;
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<string> _requests = [];

        public LoopbackServer(Func<string, string> respond)
        {
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(respond));
        }

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }

        private async Task AcceptLoopAsync(Func<string, string> respond)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(tcp, respond));
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task ServeAsync(TcpClient tcp, Func<string, string> respond)
        {
            using var _ = tcp;
            await using var stream = tcp.GetStream();
            var buffer = new byte[8192];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var path = request.ToString().Split(' ')[1];
            lock (_requests)
            {
                _requests.Add(path);
            }

            await stream.WriteAsync(Encoding.UTF8.GetBytes(respond(path)), _stop.Token);
            await stream.FlushAsync(_stop.Token);
        }
    }
}
