using System.Net;
using System.Net.Sockets;
using Beacon.AI.Services.LlmProviders;
using Beacon.Connector.Api;
using Beacon.Connector.Api.Services;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Exceptions;
using Beacon.Core.Http;
using Beacon.Core.Models;
using Beacon.Core.Notifications;
using Beacon.Tests.Unit.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Http;

/// <summary>
/// The connect-time address check of the HTTP data-source clients (REST API connector, Databricks) and the LLM clients:
/// how each address family is classified (also when carried in an IPv6 form), which kinds each client's rules refuse and
/// which options widen them, that the check runs on the addresses a host resolves to when connecting, and that a refusal
/// surfaces as the client's own error naming the rule, never the address or URL.
/// </summary>
[TestFixture]
public class OutboundAddressGuardTests
{
    [TestCase("8.8.8.8", "Public")]
    [TestCase("2606:4700:4700::1111", "Public")]
    [TestCase("127.0.0.1", "Loopback")]
    [TestCase("127.255.255.254", "Loopback")]
    [TestCase("::1", "Loopback")]
    [TestCase("::ffff:127.0.0.1", "Loopback")]
    [TestCase("169.254.169.254", "LinkLocal")]
    [TestCase("169.254.0.1", "LinkLocal")]
    [TestCase("168.63.129.16", "LinkLocal")]
    [TestCase("100.100.100.200", "LinkLocal")]
    [TestCase("fe80::1", "LinkLocal")]
    [TestCase("fd00:ec2::254", "LinkLocal")]
    [TestCase("fd20:ce::254", "LinkLocal")]
    [TestCase("::ffff:169.254.169.254", "LinkLocal")]
    [TestCase("64:ff9b::a9fe:a9fe", "LinkLocal")]
    [TestCase("2002:a9fe:a9fe::", "LinkLocal")]
    [TestCase("0.0.0.0", "NotRoutable")]
    [TestCase("::", "NotRoutable")]
    [TestCase("::ffff:0.0.0.0", "NotRoutable")]
    [TestCase("224.0.0.1", "NotRoutable")]
    [TestCase("255.255.255.255", "NotRoutable")]
    [TestCase("ff02::1", "NotRoutable")]
    [TestCase("10.0.0.5", "PrivateNetwork")]
    [TestCase("172.16.0.1", "PrivateNetwork")]
    [TestCase("172.31.255.255", "PrivateNetwork")]
    [TestCase("192.168.1.10", "PrivateNetwork")]
    [TestCase("100.64.0.1", "PrivateNetwork")]
    [TestCase("fc00::1", "PrivateNetwork")]
    [TestCase("fd12:3456::1", "PrivateNetwork")]
    [TestCase("::ffff:10.1.2.3", "PrivateNetwork")]
    public void Classify_SortsEachAddressFamily(string address, string expected)
    {
        OutboundAddresses.Classify(IPAddress.Parse(address)).Should().Be(Enum.Parse<OutboundAddressKind>(expected));
    }

    [TestCase("127.0.0.1", false, true, "Loopback")]
    [TestCase("::1", false, true, "Loopback")]
    [TestCase("::ffff:127.0.0.1", false, true, "Loopback")]
    [TestCase("127.0.0.1", true, true, null)]
    [TestCase("10.0.0.5", false, true, null)]
    [TestCase("10.0.0.5", false, false, "PrivateNetwork")]
    [TestCase("::ffff:192.168.0.1", false, false, "PrivateNetwork")]
    [TestCase("169.254.169.254", true, true, "LinkLocal")]
    [TestCase("::ffff:169.254.169.254", true, true, "LinkLocal")]
    [TestCase("fe80::1", true, true, "LinkLocal")]
    [TestCase("0.0.0.0", true, true, "NotRoutable")]
    [TestCase("::", true, true, "NotRoutable")]
    [TestCase("8.8.8.8", false, false, null)]
    public void Judge_AppliesTheRules(string address, bool allowLoopback, bool allowPrivateNetworks, string? expected)
    {
        var guard = new OutboundAddressGuard(new OutboundAddressRules(allowLoopback, allowPrivateNetworks), new FakeResolver());

        guard.Judge(IPAddress.Parse(address)).Should().Be(Refusal(expected));
    }

    [TestCase("127.0.0.1", null)]
    [TestCase("10.0.0.5", null)]
    [TestCase("169.254.169.254", "LinkLocal")]
    [TestCase("::ffff:169.254.169.254", "LinkLocal")]
    [TestCase("0.0.0.0", "NotRoutable")]
    public void TheLlmRules_AllowLoopbackAndPrivateNetworks_ButNeverLinkLocal(string address, string? expected)
    {
        var guard = new OutboundAddressGuard(OutboundAddressRules.Llm, new FakeResolver());

        guard.Judge(IPAddress.Parse(address)).Should().Be(Refusal(expected));
    }

    [Test]
    public void TheDataSourceRules_RefuseLoopback_AndAllowPrivateNetworks_ByDefault()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        DataSourceOutboundAddresses.Rules(provider).Should().Be(new OutboundAddressRules(AllowLoopback: false, AllowPrivateNetworks: true));
    }

    [Test]
    public void TheDataSourceRules_FollowAllowInsecure_AndAllowPrivateNetworks()
    {
        var services = new ServiceCollection();
        services.Configure<DataSourceTransportOptions>(x => x.AllowInsecure = true);
        services.Configure<DataSourceOutboundOptions>(x => x.AllowPrivateNetworks = false);
        using var provider = services.BuildServiceProvider();

        DataSourceOutboundAddresses.Rules(provider).Should().Be(new OutboundAddressRules(AllowLoopback: true, AllowPrivateNetworks: false));
    }

    [Test]
    public void TheDataSourceRules_BindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:DataSources:Outbound:AllowPrivateNetworks"] = "false" })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<DataSourceOutboundOptions>().Bind(configuration.GetSection(DataSourceOutboundOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        DataSourceOutboundAddresses.Rules(provider).AllowPrivateNetworks.Should().BeFalse();
    }

    [Test]
    public async Task ConnectAsync_RefusesAHostThatResolvesOnlyToRefusedAddresses_WithoutConnecting()
    {
        var resolver = new FakeResolver { ["api.example.test"] = ["169.254.169.254", "::ffff:169.254.169.254"] };
        var guard = new OutboundAddressGuard(new OutboundAddressRules(true, true), resolver);

        var act = async () => await guard.ConnectAsync(new DnsEndPoint("api.example.test", 443), checkAddresses: true, CancellationToken.None);

        (await act.Should().ThrowAsync<OutboundAddressRefusedException>()).Which.Refusal.Should().Be(OutboundAddressRefusal.LinkLocal);
    }

    [Test]
    public async Task ConnectAsync_DropsTheRefusedAddresses_AndConnectsToAnAllowedOne()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resolver = new FakeResolver { ["api.example.test"] = ["169.254.169.254", "127.0.0.1"] };
        var guard = new OutboundAddressGuard(new OutboundAddressRules(AllowLoopback: true, AllowPrivateNetworks: true), resolver);

        await using var stream = await guard.ConnectAsync(new DnsEndPoint("api.example.test", port), checkAddresses: true, CancellationToken.None);
        using var accepted = await listener.AcceptTcpClientAsync();

        accepted.Connected.Should().BeTrue();
    }

    [Test]
    public async Task ConnectAsync_JudgesEachResolution_SoALaterAnswerCannotReachARefusedAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resolver = new FakeResolver().Sequence("api.example.test", ["127.0.0.1"], ["169.254.169.254"]);
        var guard = new OutboundAddressGuard(new OutboundAddressRules(AllowLoopback: true, AllowPrivateNetworks: true), resolver);

        await using (await guard.ConnectAsync(new DnsEndPoint("api.example.test", port), checkAddresses: true, CancellationToken.None))
        {
        }

        var second = async () => await guard.ConnectAsync(new DnsEndPoint("api.example.test", port), checkAddresses: true, CancellationToken.None);

        await second.Should().ThrowAsync<OutboundAddressRefusedException>();
        resolver.Lookups.Should().Be(2);
    }

    [Test]
    public async Task EnsureDestinationAllowedAsync_RefusesWhenAnyResolvedAddressIsRefused()
    {
        var resolver = new FakeResolver { ["api.example.test"] = ["10.0.0.5", "169.254.169.254"] };
        var guard = new OutboundAddressGuard(new OutboundAddressRules(true, true), resolver);

        var act = () => guard.EnsureDestinationAllowedAsync(new Uri("https://api.example.test/x"), CancellationToken.None);

        (await act.Should().ThrowAsync<OutboundAddressRefusedException>()).Which.Refusal.Should().Be(OutboundAddressRefusal.LinkLocal);
    }

    [Test]
    public async Task TheDataSourceCheck_RefusesALoopbackHost_WithATransportErrorNamingTheRuleOnly_AndNeverConnects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var client = DataSourceClient(provider);

        var act = () => client.GetAsync($"http://127.0.0.1:{port}/rows");

        var refusal = (await act.Should().ThrowExactlyAsync<DataSourceTransportException>()).Which;
        refusal.Message.Should().Be(DataSourceOutboundAddresses.RefusalMessage(OutboundAddressRefusal.Loopback));
        refusal.Message.Should().NotContain("127.0.0.1").And.NotContain(port.ToString());
        refusal.InnerException.Should().BeNull();
        listener.Pending().Should().BeFalse();
    }

    [Test]
    public async Task TheDataSourceCheck_RefusesAMetadataAddress_EvenWithAllowInsecure()
    {
        var services = new ServiceCollection();
        services.Configure<DataSourceTransportOptions>(x => x.AllowInsecure = true);
        services.AddSingleton<IHostAddressResolver>(new FakeResolver { ["warehouse.example.test"] = ["169.254.169.254"] });
        using var provider = services.BuildServiceProvider();
        using var client = DataSourceClient(provider);

        var act = () => client.GetAsync("http://warehouse.example.test/api/2.0/sql/statements");

        (await act.Should().ThrowExactlyAsync<DataSourceTransportException>()).Which.Message
            .Should().Be(DataSourceOutboundAddresses.RefusalMessage(OutboundAddressRefusal.LinkLocal))
            .And.NotContain("warehouse.example.test");
    }

    [Test]
    public async Task TheDataSourceCheck_RefusesAPrivateNetworkAddress_WhenPrivateNetworksAreOff()
    {
        var services = new ServiceCollection();
        services.Configure<DataSourceOutboundOptions>(x => x.AllowPrivateNetworks = false);
        services.AddSingleton<IHostAddressResolver>(new FakeResolver { ["api.example.test"] = ["192.168.1.10"] });
        using var provider = services.BuildServiceProvider();
        using var client = DataSourceClient(provider);

        var act = () => client.GetAsync("http://api.example.test/rows");

        (await act.Should().ThrowExactlyAsync<DataSourceTransportException>()).Which.Message
            .Should().Contain("Beacon:DataSources:Outbound:AllowPrivateNetworks");
    }

    [Test]
    public async Task TheDataSourceCheck_ReachesALoopbackHost_WithAllowInsecure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var services = new ServiceCollection();
        services.Configure<DataSourceTransportOptions>(x => x.AllowInsecure = true);
        using var provider = services.BuildServiceProvider();
        using var client = DataSourceClient(provider);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var request = client.GetAsync($"http://127.0.0.1:{port}/rows", cancel.Token);
        using var accepted = await listener.AcceptTcpClientAsync(cancel.Token);
        await cancel.CancelAsync();

        accepted.Connected.Should().BeTrue();
        await request.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    [Test]
    public void TheApiConnectorClient_ConnectsThroughTheGuard_AndMapsItsRefusals()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new BeaconBuilder(services, new ConfigurationBuilder().Build()).AddApiConnector();
        using var provider = services.BuildServiceProvider();

        var chain = HandlerChain(provider, ApiConnectorHttpClient.Name);

        chain.OfType<SocketsHttpHandler>().Should().ContainSingle().Which.ConnectCallback.Should().NotBeNull();
        chain.Should().ContainSingle(x => x is OutboundAddressCheckHandler);
    }

    [Test]
    public void TheDatabricksClient_ConnectsThroughTheGuard_AndMapsItsRefusals()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconOutboundHttpClients();
        using var provider = services.BuildServiceProvider();

        var chain = HandlerChain(provider, BeaconHttpClients.Databricks);

        chain.OfType<SocketsHttpHandler>().Should().ContainSingle().Which.ConnectCallback.Should().NotBeNull();
        chain.Should().ContainSingle(x => x is OutboundAddressCheckHandler);
    }

    [TestCase(LlmHttpClients.OpenAi)]
    [TestCase(LlmHttpClients.AzureOpenAi)]
    [TestCase(LlmHttpClients.Claude)]
    public void EveryLlmClient_ConnectsThroughTheGuard_AndMapsItsRefusals(string name)
    {
        var services = new ServiceCollection();
        services.AddLlmHttpClients();
        using var provider = services.BuildServiceProvider();

        var chain = HandlerChain(provider, name);

        chain.OfType<SocketsHttpHandler>().Should().ContainSingle().Which.ConnectCallback.Should().NotBeNull();
        chain.Should().ContainSingle(x => x is OutboundAddressCheckHandler);
    }

    [Test]
    public async Task AnLlmClient_RefusesAMetadataAddress_WithAnAiServiceErrorNamingTheRuleOnly()
    {
        var guard = new OutboundAddressGuard(OutboundAddressRules.Llm, new FakeResolver { ["llm.example.test"] = ["169.254.169.254"] });
        var check = LlmHttpClients.CheckHandler(guard);
        check.InnerHandler = LlmHttpClients.CreatePrimaryHandler(guard);
        using var client = new HttpClient(check);

        var act = () => client.PostAsync("http://llm.example.test/v1/chat/completions", new StringContent("{}"));

        (await act.Should().ThrowExactlyAsync<AiServiceException>()).Which.Message
            .Should().Be(LlmHttpClients.RefusalMessage(OutboundAddressRefusal.LinkLocal))
            .And.NotContain("llm.example.test");
    }

    [Test]
    public void TheSharedLlmClientsHandler_ConnectsThroughTheGuard()
    {
        using var handler = LlmHttpClients.CreatePrimaryHandler();

        handler.ConnectCallback.Should().NotBeNull();
    }

    [Test]
    public async Task TheCheckHandler_KeepsRequestsOffHttp3_WhichTheConnectCallbackDoesNotSee()
    {
        var inner = new CapturingHandler();
        var check = new OutboundAddressCheckHandler(new OutboundAddressGuard(OutboundAddressRules.Llm, new FakeResolver()), x => new InvalidOperationException())
        {
            InnerHandler = inner
        };
        using var invoker = new HttpMessageInvoker(check);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://llm.example.test/")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        await invoker.SendAsync(request, CancellationToken.None);

        inner.Version.Should().Be(HttpVersion.Version20);
        inner.Policy.Should().Be(HttpVersionPolicy.RequestVersionOrLower);
    }

    [Test]
    public async Task TheCheckHandler_RefusesACallersOwnConnectRequest()
    {
        var check = new OutboundAddressCheckHandler(new OutboundAddressGuard(OutboundAddressRules.Llm, new FakeResolver()), x => new InvalidOperationException())
        {
            InnerHandler = new CapturingHandler()
        };
        using var invoker = new HttpMessageInvoker(check);
        using var request = new HttpRequestMessage(HttpMethod.Connect, "https://llm.example.test:443/");

        var act = () => invoker.SendAsync(request, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    private static OutboundAddressRefusal? Refusal(string? name)
    {
        return name == null ? null : Enum.Parse<OutboundAddressRefusal>(name);
    }

    private static HttpClient DataSourceClient(IServiceProvider provider)
    {
        var guard = DataSourceOutboundAddresses.Guard(provider);
        var check = DataSourceOutboundAddresses.CheckHandler(guard);
        check.InnerHandler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = guard.ConnectAsync
        };

        return new HttpClient(check);
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

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Version? Version { get; private set; }

        public HttpVersionPolicy? Policy { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Version = request.Version;
            Policy = request.VersionPolicy;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
