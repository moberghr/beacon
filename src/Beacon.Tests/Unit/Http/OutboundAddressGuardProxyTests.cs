using System.Net;
using System.Net.Sockets;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Exceptions;
using Beacon.Core.Http;
using Beacon.Core.Models;
using Beacon.Core.Notifications;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Http;

/// <summary>
/// The outbound address check while the system proxy carries a request: the destination is checked before the request is
/// sent (an http request in absolute form and an https request through a CONNECT tunnel alike), the connection to the
/// proxy itself is not vetted, a destination the proxy configuration bypasses is vetted at connect time, and a name the
/// Beacon host cannot resolve is left to the proxy. The proxy is a host name the test resolver maps to a closed loopback
/// port, so nothing leaves the machine.
/// </summary>
[NonParallelizable] // HttpClient.DefaultProxy is process-wide.
public class OutboundAddressGuardProxyTests
{
    private const string ProxyHost = "proxy.example.test";

    private IWebProxy _savedProxy = null!;
    private int _proxyPort;

    [SetUp]
    public void SetUp()
    {
        _savedProxy = HttpClient.DefaultProxy;
        _proxyPort = ClosedLoopbackPort();
        HttpClient.DefaultProxy = new WebProxy($"http://{ProxyHost}:{_proxyPort}", BypassOnLocal: false, BypassList: ["bypassed.example.test"]);
    }

    [TearDown]
    public void TearDown()
    {
        HttpClient.DefaultProxy = _savedProxy;
    }

    [TestCase("http://metadata.example.test/latest/meta-data")]
    [TestCase("https://metadata.example.test/latest/meta-data")]
    public async Task ARefusedDestination_IsRefusedBeforeTheRequestReachesTheProxy(string url)
    {
        var resolver = Resolver();
        using var client = DataSourceClient(resolver);

        var act = () => client.GetAsync(url);

        (await act.Should().ThrowExactlyAsync<DataSourceTransportException>()).Which.Message
            .Should().Be(DataSourceOutboundAddresses.RefusalMessage(OutboundAddressRefusal.LinkLocal))
            .And.NotContain("metadata.example.test");
        resolver.Lookups.Should().Be(1, "only the destination is resolved; the proxy is never connected to");
    }

    [TestCase("http://169.254.169.254/latest/meta-data", "LinkLocal")]
    [TestCase("https://[fe80::1]/", "LinkLocal")]
    [TestCase("http://127.0.0.1:8080/", "Loopback")]
    [TestCase("https://[::ffff:127.0.0.1]/", "Loopback")]
    public async Task ARefusedAddressLiteral_IsRefusedBeforeTheRequestReachesTheProxy(string url, string refusal)
    {
        var resolver = Resolver();
        using var client = DataSourceClient(resolver);

        var act = () => client.GetAsync(url);

        (await act.Should().ThrowExactlyAsync<DataSourceTransportException>()).Which.Message
            .Should().Be(DataSourceOutboundAddresses.RefusalMessage(Enum.Parse<OutboundAddressRefusal>(refusal)));
        resolver.Lookups.Should().Be(0);
    }

    [Test]
    public async Task ADestinationWithAnyRefusedAddress_IsRefused_ThoughAnotherOfItsAddressesIsAllowed()
    {
        var resolver = Resolver();
        resolver["mixed.example.test"] = ["8.8.8.8", "169.254.169.254"];
        using var client = DataSourceClient(resolver);

        var act = () => client.GetAsync("https://mixed.example.test/");

        await act.Should().ThrowExactlyAsync<DataSourceTransportException>();
    }

    [TestCase("http://api.example.test/v1")]
    [TestCase("https://api.example.test/v1")]
    public async Task AnAllowedDestination_GoesToTheProxy_WhoseOwnAddressIsNotVetted(string url)
    {
        // The proxy resolves to loopback, which the data-source rules refuse for a destination: reaching it (and failing
        // on its closed port) rather than a refusal shows the connection to the proxy is let through.
        var resolver = Resolver();
        using var client = DataSourceClient(resolver);

        var act = () => client.GetAsync(url);

        Causes((await act.Should().ThrowExactlyAsync<HttpRequestException>()).Which).Should().Contain(x => x is SocketException);
        resolver.Lookups.Should().Be(2, "the destination is checked, then the proxy is connected to");
    }

    [Test]
    public async Task ADestinationTheProxyBypasses_IsVettedAtConnectTime()
    {
        var resolver = Resolver();
        using var client = DataSourceClient(resolver);

        var act = () => client.GetAsync("http://bypassed.example.test/");

        (await act.Should().ThrowExactlyAsync<DataSourceTransportException>()).Which.Message
            .Should().Be(DataSourceOutboundAddresses.RefusalMessage(OutboundAddressRefusal.LinkLocal));
        resolver.Lookups.Should().Be(1, "the check before sending is skipped and the connect callback resolves the destination");
    }

    [Test]
    public async Task ANameTheBeaconHostCannotResolve_IsLeftToTheProxy()
    {
        var resolver = Resolver();
        using var client = DataSourceClient(resolver);

        var act = () => client.GetAsync("https://proxy-only.example.test/");

        Causes((await act.Should().ThrowExactlyAsync<HttpRequestException>()).Which).Should().Contain(x => x is SocketException);
    }

    [TestCase("http://metadata.example.test/")]
    [TestCase("https://metadata.example.test/")]
    public async Task AnLlmClient_RefusesALinkLocalDestination_ThroughTheProxy(string url)
    {
        var resolver = Resolver();
        var guard = new OutboundAddressGuard(OutboundAddressRules.Llm, resolver);
        var check = LlmHttpClients.CheckHandler(guard);
        check.InnerHandler = LlmHttpClients.CreatePrimaryHandler(guard);
        using var client = new HttpClient(check);

        var act = () => client.PostAsync(url, new StringContent("{}"));

        (await act.Should().ThrowExactlyAsync<AiServiceException>()).Which.Message
            .Should().Be(LlmHttpClients.RefusalMessage(OutboundAddressRefusal.LinkLocal));
        resolver.Lookups.Should().Be(1);
    }

    [Test]
    public void TheTestProxy_AppliesToTheDestinations_ButNotToTheBypassedOne()
    {
        OutboundAddresses.UsesSystemProxy(new Uri("http://metadata.example.test/")).Should().BeTrue();
        OutboundAddresses.UsesSystemProxy(new Uri("https://metadata.example.test/")).Should().BeTrue();
        OutboundAddresses.UsesSystemProxy(new Uri("http://bypassed.example.test/")).Should().BeFalse();
    }

    private FakeResolver Resolver()
    {
        return new FakeResolver
        {
            [ProxyHost] = ["127.0.0.1"],
            ["metadata.example.test"] = ["169.254.169.254"],
            ["bypassed.example.test"] = ["169.254.169.254"],
            ["api.example.test"] = ["8.8.8.8"]
        };
    }

    private static HttpClient DataSourceClient(FakeResolver resolver)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostAddressResolver>(resolver);
        using var provider = services.BuildServiceProvider();
        var guard = DataSourceOutboundAddresses.Guard(provider);
        var check = DataSourceOutboundAddresses.CheckHandler(guard);
        check.InnerHandler = BeaconHttpClients.CreateDataSourceHandler(guard);

        return new HttpClient(check);
    }

    private static List<Exception> Causes(Exception exception)
    {
        var causes = new List<Exception>();
        for (var current = exception.InnerException; current != null; current = current.InnerException)
        {
            causes.Add(current);
        }

        return causes;
    }

    // A loopback port nothing listens on: a connection to it is refused at once.
    private static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
