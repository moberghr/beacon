using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// The address check behind every notification call: which addresses count as public (each blocked range at both edges
/// and just outside them, and IPv4 carried in IPv6 forms), how a type's private-network allowances widen it (never for
/// another type, never to metadata for host-name entries), and how the connect callback and the proxy-mode check use it.
/// </summary>
[TestFixture]
public class OutboundAddressPolicyTests
{
    // First and last address of every blocked range, and the addresses just outside it.
    [TestCase("0.0.0.0", false)]
    [TestCase("0.255.255.255", false)]
    [TestCase("1.0.0.0", true)]
    [TestCase("9.255.255.255", true)]
    [TestCase("10.0.0.0", false)]
    [TestCase("10.255.255.255", false)]
    [TestCase("11.0.0.0", true)]
    [TestCase("100.63.255.255", true)]
    [TestCase("100.64.0.0", false)]
    [TestCase("100.127.255.255", false)]
    [TestCase("100.128.0.0", true)]
    [TestCase("126.255.255.255", true)]
    [TestCase("127.0.0.0", false)]
    [TestCase("127.255.255.255", false)]
    [TestCase("128.0.0.0", true)]
    [TestCase("168.63.129.15", true)]
    [TestCase("168.63.129.16", false)]
    [TestCase("168.63.129.17", true)]
    [TestCase("169.253.255.255", true)]
    [TestCase("169.254.0.0", false)]
    [TestCase("169.254.169.254", false)]
    [TestCase("169.254.255.255", false)]
    [TestCase("169.255.0.0", true)]
    [TestCase("172.15.255.255", true)]
    [TestCase("172.16.0.0", false)]
    [TestCase("172.31.255.255", false)]
    [TestCase("172.32.0.0", true)]
    [TestCase("191.255.255.255", true)]
    [TestCase("192.0.0.0", false)]
    [TestCase("192.0.0.255", false)]
    [TestCase("192.0.1.0", true)]
    [TestCase("192.0.1.255", true)]
    [TestCase("192.0.2.0", false)]
    [TestCase("192.0.2.255", false)]
    [TestCase("192.0.3.0", true)]
    [TestCase("192.88.98.255", true)]
    [TestCase("192.88.99.0", false)]
    [TestCase("192.88.99.255", false)]
    [TestCase("192.88.100.0", true)]
    [TestCase("192.167.255.255", true)]
    [TestCase("192.168.0.0", false)]
    [TestCase("192.168.255.255", false)]
    [TestCase("192.169.0.0", true)]
    [TestCase("198.17.255.255", true)]
    [TestCase("198.18.0.0", false)]
    [TestCase("198.19.255.255", false)]
    [TestCase("198.20.0.0", true)]
    [TestCase("198.51.99.255", true)]
    [TestCase("198.51.100.0", false)]
    [TestCase("198.51.100.255", false)]
    [TestCase("198.51.101.0", true)]
    [TestCase("203.0.112.255", true)]
    [TestCase("203.0.113.0", false)]
    [TestCase("203.0.113.255", false)]
    [TestCase("203.0.114.0", true)]
    [TestCase("223.255.255.255", true)]
    [TestCase("224.0.0.0", false)]
    [TestCase("239.255.255.255", false)]
    [TestCase("240.0.0.0", false)]
    [TestCase("255.255.255.255", false)]
    public void IPv4_RangeEdges(string address, bool isPublic)
    {
        OutboundAddressPolicy.IsPublic(IPAddress.Parse(address)).Should().Be(isPublic);
    }

    [TestCase("::", false)]
    [TestCase("::1", false)]
    [TestCase("::ffff:ffff", false)]
    [TestCase("::1:0:0", false)]
    [TestCase("::ffff:0:a00:1", false)]
    [TestCase("::ffff:0:808:808", false)]
    [TestCase("64:ff9b:1::", false)]
    [TestCase("64:ff9b:1:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("64:ff9b:2::", false)]
    [TestCase("ff::ffff:ffff:ffff:ffff", false)]
    [TestCase("100::", false)]
    [TestCase("100::ffff:ffff:ffff:ffff", false)]
    [TestCase("100:0:0:1::", false)]
    [TestCase("1fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("2000::", true)]
    [TestCase("2000:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [TestCase("3fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [TestCase("4000::", false)]
    [TestCase("2001::", false)]
    [TestCase("2001:0:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("2001:1::1", true)]
    [TestCase("2001:2::", false)]
    [TestCase("2001:2:0:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("2001:2:1::", true)]
    [TestCase("2001:f:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [TestCase("2001:10::", false)]
    [TestCase("2001:1f:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("2001:20::", false)]
    [TestCase("2001:2f:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("2001:30::", true)]
    [TestCase("2001:db7:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [TestCase("2001:db8::", false)]
    [TestCase("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("2001:db9::", true)]
    [TestCase("3ffe:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [TestCase("3fff::", false)]
    [TestCase("3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("3fff:1000::", true)]
    [TestCase("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("fc00::", false)]
    [TestCase("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("fe00::", false)]
    [TestCase("fe7f:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("fe80::", false)]
    [TestCase("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("fec0::", false)]
    [TestCase("feff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("ff00::", false)]
    [TestCase("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [TestCase("2606:4700:4700::1111", true)]
    public void IPv6_RangeEdges_PublicOnlyInsideGlobalUnicast(string address, bool isPublic)
    {
        OutboundAddressPolicy.IsPublic(IPAddress.Parse(address)).Should().Be(isPublic);
    }

    // IPv4 carried in IPv6 is judged by the IPv4 address, at the edges of the embedded payload too.
    [TestCase("::ffff:127.0.0.1", false)]
    [TestCase("::ffff:169.254.0.1", false)]
    [TestCase("::ffff:9.255.255.255", true)]
    [TestCase("::ffff:10.0.0.0", false)]
    [TestCase("::ffff:11.0.0.0", true)]
    [TestCase("64:ff9b::7f00:1", false)]
    [TestCase("64:ff9b::a9fe:a9fe", false)]
    [TestCase("64:ff9b::a83f:8110", false)]
    [TestCase("64:ff9b::0:0", false)]
    [TestCase("64:ff9b::ffff:ffff", false)]
    [TestCase("64:ff9b::808:808", true)]
    [TestCase("2002:7f00:1::1", false)]
    [TestCase("2002:a9fe:a9fe::1", false)]
    [TestCase("2002:c0a8:101::1", false)]
    [TestCase("2002::1", false)]
    [TestCase("2002:808:808::1", true)]
    [TestCase("2002:ffff:ffff::1", false)]
    public void IPv6_CarryingIPv4_IsJudgedByTheIPv4Address(string address, bool isPublic)
    {
        OutboundAddressPolicy.IsPublic(IPAddress.Parse(address)).Should().Be(isPublic);
    }

    [Test]
    public void ConfiguredNat64Prefix_IsJudgedByTheEmbeddedAddress()
    {
        var policy = NotificationTestKit.AddressPolicy(new NotificationChannelOptions { Nat64Prefixes = ["2001:db8:64::/96"] });

        policy.IsPublicAddress(IPAddress.Parse("2001:db8:64::808:808")).Should().BeTrue("the prefix carries 8.8.8.8");
        policy.IsPublicAddress(IPAddress.Parse("2001:db8:64::a9fe:a9fe")).Should().BeFalse();
        OutboundAddressPolicy.IsPublic(IPAddress.Parse("2001:db8:64::808:808")).Should().BeFalse("without the prefix it is documentation space");
    }

    [Test]
    public void PrivateNetwork_ForOneType_NeverOpensAnother()
    {
        var policy = NotificationTestKit.AddressPolicy(NotificationTestKit.PrivateNetworks(NotificationType.Jira, "10.20.0.0/16", "jira.bank.internal"));

        policy.IsAllowed(NotificationType.Jira, "x", IPAddress.Parse("10.20.1.2")).Should().BeTrue();
        policy.IsAllowed(NotificationType.Jira, "jira.bank.internal", IPAddress.Parse("10.9.9.9")).Should().BeTrue();
        policy.IsAllowed(NotificationType.Webhook, "x", IPAddress.Parse("10.20.1.2")).Should().BeFalse();
        policy.IsAllowed(NotificationType.Webhook, "jira.bank.internal", IPAddress.Parse("10.9.9.9")).Should().BeFalse();
        policy.IsAllowed(NotificationType.Slack, "x", IPAddress.Parse("10.20.1.2")).Should().BeFalse();
    }

    [Test]
    public void PrivateNetwork_Cidr_AllowsOnlyAddressesInsideIt()
    {
        var policy = NotificationTestKit.AddressPolicy(NotificationTestKit.PrivateNetworks(NotificationType.Webhook, "10.20.0.0/16", "fd00::5"));

        policy.IsAllowed(NotificationType.Webhook, "h", IPAddress.Parse("10.20.5.6")).Should().BeTrue();
        policy.IsAllowed(NotificationType.Webhook, "h", IPAddress.Parse("::ffff:10.20.5.6")).Should().BeTrue("a mapped literal is checked as its IPv4 address");
        policy.IsAllowed(NotificationType.Webhook, "h", IPAddress.Parse("64:ff9b::a14:506")).Should().BeTrue("so is a NAT64 one");
        policy.IsAllowed(NotificationType.Webhook, "h", IPAddress.Parse("10.21.0.1")).Should().BeFalse();
        policy.IsAllowed(NotificationType.Webhook, "h", IPAddress.Parse("fd00::5")).Should().BeTrue();
        policy.IsAllowed(NotificationType.Webhook, "h", IPAddress.Parse("fd00::6")).Should().BeFalse();
        policy.IsAllowed(NotificationType.Webhook, "h", IPAddress.Parse("93.184.216.34")).Should().BeTrue("public addresses stay allowed");
    }

    [Test]
    public void PrivateNetwork_HostName_AllowsWhatThatHostResolvesTo()
    {
        var policy = NotificationTestKit.AddressPolicy(NotificationTestKit.PrivateNetworks(NotificationType.Jira, "jira.bank.internal", "*.corp.internal"));

        policy.IsAllowed(NotificationType.Jira, "jira.bank.internal", IPAddress.Parse("10.9.9.9")).Should().BeTrue();
        policy.IsAllowed(NotificationType.Jira, "JIRA.bank.internal.", IPAddress.Parse("127.0.0.1")).Should().BeTrue();
        policy.IsAllowed(NotificationType.Jira, "hooks.corp.internal", IPAddress.Parse("fd12::1")).Should().BeTrue();
        policy.IsAllowed(NotificationType.Jira, "corp.internal", IPAddress.Parse("192.168.1.1")).Should().BeFalse("a wildcard does not match the domain itself");
        policy.IsAllowed(NotificationType.Jira, "other-jira.bank.internal.example", IPAddress.Parse("10.9.9.9")).Should().BeFalse();
    }

    // A host-name entry never reaches link-local, metadata or the Azure wire server, in any IPv6 wrapping either.
    [TestCase("169.254.169.254")]
    [TestCase("169.254.0.1")]
    [TestCase("168.63.129.16")]
    [TestCase("::ffff:169.254.169.254")]
    [TestCase("::ffff:169.254.0.1")]
    [TestCase("::ffff:0:a9fe:a9fe")]
    [TestCase("::a9fe:a9fe")]
    [TestCase("64:ff9b::a9fe:a9fe")]
    [TestCase("64:ff9b::a83f:8110")]
    [TestCase("64:ff9b:1::a9fe:a9fe")]
    [TestCase("64:ff9b:1:a9fe:a9fe::")]
    [TestCase("2002:a9fe:a9fe::1")]
    [TestCase("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [TestCase("fe80::1")]
    [TestCase("fd00:ec2::254")]
    public void PrivateNetwork_HostName_NeverReachesMetadata(string address)
    {
        var policy = NotificationTestKit.AddressPolicy(NotificationTestKit.PrivateNetworks(NotificationType.Jira, "jira.bank.internal"));

        policy.IsAllowed(NotificationType.Jira, "jira.bank.internal", IPAddress.Parse(address)).Should().BeFalse();
    }

    [Test]
    public void PrivateNetwork_HostName_ThroughAConfiguredNat64Prefix_NeverReachesMetadata()
    {
        var options = NotificationTestKit.PrivateNetworks(NotificationType.Jira, "jira.bank.internal");
        options.Nat64Prefixes.Add("2001:db8:64::/96");
        var policy = NotificationTestKit.AddressPolicy(options);

        policy.IsAllowed(NotificationType.Jira, "jira.bank.internal", IPAddress.Parse("2001:db8:64::a9fe:a9fe")).Should().BeFalse();
        policy.IsAllowed(NotificationType.Jira, "jira.bank.internal", IPAddress.Parse("2001:db8:64::a14:102")).Should().BeTrue();
    }

    [Test]
    public void PrivateNetwork_ExplicitRange_CanReachLinkLocal()
    {
        var policy = NotificationTestKit.AddressPolicy(NotificationTestKit.PrivateNetworks(NotificationType.Webhook, "169.254.10.0/24"));

        policy.IsAllowed(NotificationType.Webhook, "appliance", IPAddress.Parse("169.254.10.5")).Should().BeTrue("an operator who names the range means it");
        policy.IsAllowed(NotificationType.Webhook, "appliance", IPAddress.Parse("169.254.169.254")).Should().BeFalse();
    }

    [TestCase("10.0.0.0/8", true)]
    [TestCase("10.1.2.3", true)]
    [TestCase("fd00::/8", true)]
    [TestCase("jira.bank.internal", true)]
    [TestCase("*.bank.internal", true)]
    [TestCase("10.1.2.3/8", false)]
    [TestCase("10.0.0.0/33", false)]
    [TestCase("", false)]
    [TestCase("*", false)]
    [TestCase("*.com", false)]
    [TestCase("*.internal", false)]
    [TestCase("http://jira", false)]
    public void AllowListEntries_AreValidated(string entry, bool valid)
    {
        OutboundAddressPolicy.IsValidAllowListEntry(entry).Should().Be(valid);
    }

    [TestCase("2001:db8:64::/96", true)]
    [TestCase("64:ff9b::/96", true)]
    [TestCase("2001:db8:64::/64", false)]
    [TestCase("2001:db8:64::1/96", false)]
    [TestCase("10.0.0.0/8", false)]
    [TestCase("2001:db8:64::", false)]
    [TestCase("nat64", false)]
    public void Nat64Prefixes_AreValidated(string entry, bool valid)
    {
        OutboundAddressPolicy.IsValidNat64Prefix(entry).Should().Be(valid);
    }

    // --- connecting ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Connect_ToLoopback_IsRefusedBeforeConnecting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var act = async () => await NotificationTestKit.AddressPolicy().ConnectAsync(NotificationType.Webhook, new DnsEndPoint("127.0.0.1", port), checkAddresses: true, CancellationToken.None);

        await act.Should().ThrowAsync<OutboundConnectionBlockedException>();
        listener.Pending().Should().BeFalse("no connection may reach the listener");
    }

    [TestCase("localhost")]
    [TestCase("[::1]")]
    public async Task Connect_ToALoopbackName_IsRefused(string host)
    {
        var resolver = new FakeResolver { ["localhost"] = ["127.0.0.1", "::1"] };

        var act = async () => await NotificationTestKit.AddressPolicy(resolver: resolver).ConnectAsync(NotificationType.Webhook, new DnsEndPoint(host, 9), checkAddresses: true, CancellationToken.None);

        await act.Should().ThrowAsync<OutboundConnectionBlockedException>();
    }

    [Test]
    public async Task Connect_MixedAnswer_ConnectsOnlyToTheAllowedAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resolver = new FakeResolver { ["hooks.example.com"] = ["10.0.0.5", "127.0.0.1"] };
        var policy = NotificationTestKit.AddressPolicy(NotificationTestKit.PrivateNetworks(NotificationType.Webhook, "127.0.0.1"), resolver);

        await using var stream = await policy.ConnectAsync(NotificationType.Webhook, new DnsEndPoint("hooks.example.com", port), checkAddresses: true, CancellationToken.None);
        using var accepted = await listener.AcceptTcpClientAsync();

        ((IPEndPoint)accepted.Client.RemoteEndPoint!).Address.Should().Be(IPAddress.Loopback, "10.0.0.5 is never tried");
    }

    [Test]
    public async Task Connect_EachConnection_ChecksTheAnswerAgain()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resolver = new FakeResolver().Sequence("hooks.example.com", ["127.0.0.1"], ["10.0.0.5"]);
        var policy = NotificationTestKit.AddressPolicy(NotificationTestKit.PrivateNetworks(NotificationType.Webhook, "127.0.0.1"), resolver);

        await using var first = await policy.ConnectAsync(NotificationType.Webhook, new DnsEndPoint("hooks.example.com", port), checkAddresses: true, CancellationToken.None);
        var second = async () => await policy.ConnectAsync(NotificationType.Webhook, new DnsEndPoint("hooks.example.com", port), checkAddresses: true, CancellationToken.None);

        await second.Should().ThrowAsync<OutboundConnectionBlockedException>("a changed answer is checked again, not trusted");
        resolver.Lookups.Should().Be(2);
    }

    [Test]
    public async Task Connect_UnresolvableHost_FailsAsAConnectionFailure()
    {
        var policy = NotificationTestKit.AddressPolicy();

        var act = async () => await policy.ConnectAsync(NotificationType.Webhook, new DnsEndPoint("unknown.example.com", 443), checkAddresses: true, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<SocketException>();
        NotificationFailureReasons.For(new HttpRequestException("wrapped by the handler", thrown.Which))
            .Should().Be(NotificationFailureReasons.ConnectionFailed);
    }

    // --- proxy mode: the destination is checked before sending ---------------------------------------------------

    [Test]
    public async Task ProxyMode_PublicDestination_IsAllowed()
    {
        var policy = ProxyPolicy(new FakeResolver { ["hooks.example.com"] = ["93.184.216.34", "2606:2800:220:1::1"] });

        var act = () => policy.EnsureDestinationAllowedAsync(NotificationType.Webhook, new Uri("https://hooks.example.com/in"), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [TestCase("10.0.0.5")]
    [TestCase("169.254.169.254")]
    [TestCase("127.0.0.1")]
    [TestCase("fd00:ec2::254")]
    [TestCase("::ffff:192.168.1.1")]
    [TestCase("64:ff9b::a9fe:a9fe")]
    public async Task ProxyMode_DestinationResolvingToABlockedAddress_IsRefused(string address)
    {
        var policy = ProxyPolicy(new FakeResolver { ["hooks.example.com"] = [address] });

        var act = () => policy.EnsureDestinationAllowedAsync(NotificationType.Webhook, new Uri("https://hooks.example.com/in"), CancellationToken.None);

        await act.Should().ThrowAsync<OutboundConnectionBlockedException>();
    }

    [Test]
    public async Task ProxyMode_DestinationWithAnyBlockedAddress_IsRefused()
    {
        var policy = ProxyPolicy(new FakeResolver { ["hooks.example.com"] = ["93.184.216.34", "10.0.0.5"] });

        var act = () => policy.EnsureDestinationAllowedAsync(NotificationType.Webhook, new Uri("https://hooks.example.com/in"), CancellationToken.None);

        await act.Should().ThrowAsync<OutboundConnectionBlockedException>("the proxy may resolve the name to either address");
    }

    [Test]
    public async Task ProxyMode_DestinationThatDoesNotResolve_IsRefused()
    {
        var policy = ProxyPolicy(new FakeResolver());

        var act = () => policy.EnsureDestinationAllowedAsync(NotificationType.Webhook, new Uri("https://unknown.example.com/in"), CancellationToken.None);

        await act.Should().ThrowAsync<OutboundConnectionBlockedException>();
    }

    [Test]
    public async Task ProxyMode_PrivateDestination_IsAllowedOnlyForItsType()
    {
        var options = NotificationTestKit.PrivateNetworks(NotificationType.Jira, "10.20.0.0/16");
        options.UseSystemProxy = true;
        var policy = NotificationTestKit.AddressPolicy(options, new FakeResolver { ["jira.bank.internal"] = ["10.20.1.2"] });

        var jira = () => policy.EnsureDestinationAllowedAsync(NotificationType.Jira, new Uri("https://jira.bank.internal/rest"), CancellationToken.None);
        var webhook = () => policy.EnsureDestinationAllowedAsync(NotificationType.Webhook, new Uri("https://jira.bank.internal/rest"), CancellationToken.None);

        await jira.Should().NotThrowAsync();
        await webhook.Should().ThrowAsync<OutboundConnectionBlockedException>();
    }

    [TestCase("https://127.0.0.1/in")]
    [TestCase("https://[::1]/in")]
    public async Task ProxyMode_BlockedAddressLiteral_IsRefusedWithoutResolving(string url)
    {
        var resolver = new FakeResolver();
        var policy = ProxyPolicy(resolver);

        var act = () => policy.EnsureDestinationAllowedAsync(NotificationType.Webhook, new Uri(url), CancellationToken.None);

        await act.Should().ThrowAsync<OutboundConnectionBlockedException>();
        resolver.Lookups.Should().Be(0);
    }

    [TestCase("https://hooks.example.com/in", "hooks.example.com", 443, true)]
    [TestCase("https://HOOKS.example.com/in", "hooks.example.com", 443, true)]
    [TestCase("http://hooks.example.com:8080/in", "hooks.example.com", 8080, true)]
    [TestCase("https://[2606:2800:220:1::1]/in", "2606:2800:220:1::1", 443, true)]
    [TestCase("https://hooks.example.com/in", "proxy.bank.internal", 3128, false)]
    [TestCase("https://hooks.example.com/in", "hooks.example.com", 3128, false)]
    public void ProxyMode_ConnectionToTheRequestHostIsTheDestination_AnyOtherIsTheProxy(string url, string host, int port, bool isDestination)
    {
        OutboundAddressPolicy.IsDestination(new Uri(url), new DnsEndPoint(host, port)).Should().Be(isDestination);
    }

    [Test]
    public async Task ProxyMode_HttpsTunnelToAPrivateProxy_IsNotVetted()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var policy = ProxyPolicy(new FakeResolver());

        // What the handler does for an https call through a proxy: a CONNECT request addressed to the proxy itself.
        var tunnel = await ConnectThroughCallbackAsync(policy, new HttpRequestMessage(HttpMethod.Connect, $"http://127.0.0.1:{port}"), port);
        await using var stream = tunnel;
        using var accepted = await listener.AcceptTcpClientAsync();

        accepted.Connected.Should().BeTrue();
    }

    [Test]
    public void UsesSystemProxy_FollowsTheOption()
    {
        ProxyPolicy(new FakeResolver()).UsesSystemProxy.Should().BeTrue();
        NotificationTestKit.AddressPolicy().UsesSystemProxy.Should().BeFalse();
    }

    // Drives the real connect callback the way SocketsHttpHandler does, through a handler whose callback is the policy's.
    private static async Task<Stream> ConnectThroughCallbackAsync(OutboundAddressPolicy policy, HttpRequestMessage request, int port)
    {
        Stream? connected = null;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                connected = await policy.ConnectAsync(NotificationType.Webhook, context, cancellationToken);
                return connected;
            },
        };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        try
        {
            await invoker.SendAsync(request, cancel.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // The listener never answers; only the connection matters.
        }

        return connected ?? throw new AssertionException("The callback refused the connection.");
    }

    private static OutboundAddressPolicy ProxyPolicy(FakeResolver resolver)
    {
        return NotificationTestKit.AddressPolicy(new NotificationChannelOptions { UseSystemProxy = true }, resolver);
    }
}
