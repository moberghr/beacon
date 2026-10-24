using Beacon.Core.Helpers;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class BaseUrlPolicyTests
{
    [TestCase("https://beacon.example", false)]
    [TestCase("https://beacon.example/app/", false)]
    [TestCase("  https://beacon.example  ", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    [TestCase("http://localhost:5000", true)]
    [TestCase("http://127.0.0.1:5000", true)]
    [TestCase("http://[::1]:5000", true)]
    public void AcceptedValues_Pass(string? value, bool development)
    {
        BaseUrlPolicy.Validate(value, development).Should().BeNull();
    }

    [TestCase("beacon.example", false)]
    [TestCase("/relative", false)]
    [TestCase("ftp://beacon.example", false)]
    [TestCase("javascript:alert(1)", false)]
    [TestCase("http://beacon.example", false)]
    [TestCase("http://beacon.example", true)]
    [TestCase("http://localhost:5000", false)]
    [TestCase("https://user:secret@beacon.example", false)]
    [TestCase("https://user@beacon.example", false)]
    [TestCase("https://beacon.example/#frag", false)]
    [TestCase("https://beacon.example#", false)]
    public void RejectedValues_AreRefused(string value, bool development)
    {
        BaseUrlPolicy.Validate(value, development).Should().Be(BaseUrlPolicy.InvalidMessage);
    }

    [TestCase("https://beacon.example/", "notifications/4", "https://beacon.example/notifications/4")]
    [TestCase("https://beacon.example/app", "notifications/4", "https://beacon.example/app/notifications/4")]
    [TestCase("not a url", "notifications/4", null)]
    [TestCase("https://user:pw@beacon.example", "notifications/4", null)]
    [TestCase("", "notifications/4", null)]
    [TestCase(null, "notifications/4", null)]
    public void Link_IsBuiltOnlyFromAUsableBaseUrl(string? baseUrl, string path, string? expected)
    {
        BaseUrlPolicy.Link(baseUrl, path).Should().Be(expected);
    }

    [TestCase("http://beacon.example", false)]
    [TestCase("http://beacon.example", true)]
    [TestCase("http://localhost:5000", false)]
    [TestCase("javascript:alert(1)", true)]
    public void Link_IsLeftOutForAValueThatIsNoLongerSaveable(string baseUrl, bool development)
    {
        BaseUrlPolicy.Link(baseUrl, "notifications/4", development).Should().BeNull();
    }

    [Test]
    public void Link_AllowsPlainHttpOnlyForLoopbackInDevelopment()
    {
        BaseUrlPolicy.Link("http://localhost:5000", "notifications/4", true).Should().Be("http://localhost:5000/notifications/4");
    }

    [TestCase("https://beacon.example/b?x=1", "notifications/4", "https://beacon.example/b/notifications/4?x=1")]
    [TestCase("https://beacon.example/b/?x=1&y=2", "notifications/4", "https://beacon.example/b/notifications/4?x=1&y=2")]
    [TestCase("https://beacon.example?x=1", "notifications/4?z=3", "https://beacon.example/notifications/4?x=1&z=3")]
    [TestCase("https://beacon.example/b", "notifications/4?z=3", "https://beacon.example/b/notifications/4?z=3")]
    public void Link_KeepsOrMergesAQueryStringAtTheEnd(string baseUrl, string path, string expected)
    {
        BaseUrlPolicy.Link(baseUrl, path).Should().Be(expected);
    }

    [TestCase("https://beacon.example.", "https://beacon.example./notifications/4")]
    [TestCase("https://bücher.example/app", "https://bücher.example/app/notifications/4")]
    [TestCase("https://192.0.2.10:8443/app", "https://192.0.2.10:8443/app/notifications/4")]
    [TestCase("https://[2001:db8::1]/app", "https://[2001:db8::1]/app/notifications/4")]
    public void Link_HandlesTrailingDotIdnAndIpHosts(string baseUrl, string expected)
    {
        BaseUrlPolicy.Link(baseUrl, "notifications/4").Should().Be(expected);
    }

    [TestCase("http://127.0.0.2:5000")]
    [TestCase("http://LOCALHOST:5000")]
    [TestCase("http://localhost.:5000")]
    [TestCase("http://[::ffff:127.0.0.1]:5000")]
    [TestCase("http://2130706433:5000")]
    [TestCase("http://0x7f.0.0.1:5000")]
    public void LoopbackForms_AreAcceptedOnlyInDevelopment(string value)
    {
        BaseUrlPolicy.Validate(value, true).Should().BeNull();
        BaseUrlPolicy.Validate(value, false).Should().Be(BaseUrlPolicy.InvalidMessage);
    }

    [TestCase("http://10.0.0.1:5000")]
    [TestCase("http://beacon.example.:5000")]
    public void NonLoopbackHosts_NeverGetPlainHttp(string value)
    {
        BaseUrlPolicy.Validate(value, true).Should().NotBeNull().And.Be(BaseUrlPolicy.InvalidMessage);
    }
}
